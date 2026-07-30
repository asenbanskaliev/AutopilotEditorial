using System.Security.Cryptography;
using System.Text;
using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class BetaReaderReviewJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "beta-reader-review.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 33, "Beta reader migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 30, 4, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var project = Guid.NewGuid(); var plan = Guid.NewGuid(); var authority = Guid.NewGuid(); const long authorityRevision = 5;
        var authorityDigest = Hash($"{workspace}:{authority:D}:{authorityRevision}:APPROVED");
        SeedAuthority(factory, workspace, project, plan, authority, authorityRevision, now);
        var reviewId = Guid.NewGuid(); Guid approvedMessage;

        await using (var store = new SqliteBetaReaderReviewStore(factory))
        {
            var draft = new BetaReaderReviewDraft(reviewId, project, workspace, plan, authority, authorityRevision, authorityDigest, 1, "adult-commercial-fiction-v1", "beta-reader-v1", "beta-lead", "{\"chapters\":[1,2],\"readers\":6}", "create-beta-review");
            var created = await store.CreateAsync(draft, now.AddMinutes(1));
            Require(!created.Replayed && created.Review.Status == BetaReaderReviewStatus.Proposed && created.Review.Revision == 1, "Create failed.");
            Require((await store.CreateAsync(draft, now.AddMinutes(2))).Replayed, "Create replay failed.");
            await Throws<BetaReaderConflictException>(() => store.CreateAsync(draft with { Actor = "other-lead" }, now.AddMinutes(2)).AsTask());

            var blocking = new[] { new BetaReaderFindingDraft(Guid.NewGuid(), BetaReaderFindingArea.Comprehension, BetaReaderSeverity.Blocking, "critical-confusion", "chapter-2/scene-4", new[] { 2 }, new[] { "scene-4" }, new[] { "p-7" }, "Readers cannot identify why the protagonist changes course.", "Five of six readers reported the same causal gap.") };
            var evaluatedBlocking = await store.EvaluateAsync(new BetaReaderEvaluateCommand(Guid.NewGuid(), workspace, reviewId, 1, blocking, "reader panel evidence", "beta-lead", "evaluate-blocking-beta"), now.AddMinutes(3));
            Require(evaluatedBlocking.Status == BetaReaderReviewStatus.Evaluated && evaluatedBlocking.Revision == 2, "Blocking evaluation failed.");
            await Throws<BetaReaderTransitionException>(() => store.DecideAsync(new BetaReaderDecisionCommand(Guid.NewGuid(), workspace, reviewId, 2, BetaReaderDecision.Approve, "Approve despite blocker.", null, "beta-lead", "invalid-beta-approval"), now.AddMinutes(4)).AsTask());

            var repair = await store.DecideAsync(new BetaReaderDecisionCommand(Guid.NewGuid(), workspace, reviewId, 2, BetaReaderDecision.ReturnToRepair, "Repair the causal gap.", 2, "beta-lead", "return-beta-repair"), now.AddMinutes(5));
            Require(repair.Status == BetaReaderReviewStatus.RepairRequired && repair.Revision == 3, "Repair failed.");

            var findings = new[]
            {
                new BetaReaderFindingDraft(Guid.NewGuid(), BetaReaderFindingArea.Engagement, BetaReaderSeverity.Minor, "midpoint-energy", "chapter-1/scene-3", new[] { 1 }, new[] { "scene-3" }, new[] { "p-9" }, "One reader noted a brief energy dip.", "The observation was not repeated and is non-blocking.", false),
                new BetaReaderFindingDraft(Guid.NewGuid(), BetaReaderFindingArea.Satisfaction, BetaReaderSeverity.Info, "ending-payoff", "chapter-2/scene-5", new[] { 2 }, new[] { "scene-5" }, new[] { "p-12" }, "All readers understood and accepted the ending payoff.", "Six of six readers rated the ending coherent.", false)
            };
            var evaluate = new BetaReaderEvaluateCommand(Guid.NewGuid(), workspace, reviewId, 3, findings, "repaired panel evidence", "beta-lead", "evaluate-repaired-beta");
            var evaluated = await store.EvaluateAsync(evaluate, now.AddMinutes(6));
            Require(evaluated.Status == BetaReaderReviewStatus.Evaluated && evaluated.Revision == 4 && evaluated.Findings.Count == 2, "Re-evaluation failed.");
            Require((await store.EvaluateAsync(evaluate, now.AddMinutes(7))).Revision == 4, "Evaluation replay changed state.");

            var decide = new BetaReaderDecisionCommand(Guid.NewGuid(), workspace, reviewId, 4, BetaReaderDecision.Approve, "Beta reader panel is complete and non-blocking.", null, "beta-lead", "approve-beta-review");
            var approved = await store.DecideAsync(decide, now.AddMinutes(8));
            Require(approved.Status == BetaReaderReviewStatus.Approved && approved.Revision == 5, "Approval failed.");
            approvedMessage = approved.MessageId ?? throw new InvalidOperationException("Approval message missing.");
            Require((await store.DecideAsync(decide, now.AddMinutes(9))).Revision == 5, "Decision replay changed state.");
            Require(await store.GetAsync("workspace-b", reviewId) is null, "Workspace isolation failed.");
        }

        await using (var restarted = new SqliteBetaReaderReviewStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, reviewId) ?? throw new InvalidOperationException("Review missing after restart.");
            Require(durable.Status == BetaReaderReviewStatus.Approved && durable.Revision == 5 && durable.Findings.Count == 2, "Restart durability failed.");
        }

        using (var c = factory.OpenConnection())
        {
            using var history = c.CreateCommand();
            history.CommandText = "SELECT COUNT(*) FROM beta_reader_history WHERE workspace_id=$w AND review_id=$id;";
            history.Parameters.AddWithValue("$w", workspace); history.Parameters.AddWithValue("$id", reviewId.ToString("D"));
            Require(Convert.ToInt32(history.ExecuteScalar()) == 5, "History is not append-only exactly once.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("beta-reader-worker", 40, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == approvedMessage && x.EventType == "editorial.beta-reader.approved") == 1, "Approval event was not exactly once.");
    }

    private static void SeedAuthority(SqliteConnectionFactory factory, string workspace, Guid project, Guid plan, Guid review, long revision, DateTimeOffset at)
    {
        using var c = factory.OpenConnection(); using var tx = c.BeginTransaction();
        using (var p = c.CreateCommand())
        {
            p.Transaction = tx;
            p.CommandText = "INSERT INTO editorial_pass_plans(workspace_id,plan_id,project_id,cross_chapter_audit_id,expected_audit_revision,expected_audit_digest,version,actor,revision,status,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,1,'audit-digest',1,'lead-editor',8,'IN_PROGRESS',NULL,$at,$at);";
            p.Parameters.AddWithValue("$w", workspace); p.Parameters.AddWithValue("$id", plan.ToString("D")); p.Parameters.AddWithValue("$p", project.ToString("D")); p.Parameters.AddWithValue("$a", Guid.NewGuid().ToString("D")); p.Parameters.AddWithValue("$at", at.ToString("O")); p.ExecuteNonQuery();
        }
        using (var n = c.CreateCommand())
        {
            n.Transaction = tx;
            n.CommandText = "INSERT INTO editorial_pass_nodes(workspace_id,plan_id,pass_kind,ordinal,dependencies_json,status,attempts,gate_result,evidence,result,responsible,started_at_utc,completed_at_utc) VALUES($w,$id,'BETAREADERS',6,'[5]','READY',0,NULL,NULL,NULL,NULL,NULL,NULL);";
            n.Parameters.AddWithValue("$w", workspace); n.Parameters.AddWithValue("$id", plan.ToString("D")); n.ExecuteNonQuery();
        }
        using (var r = c.CreateCommand())
        {
            r.Transaction = tx;
            r.CommandText = "INSERT INTO copyedit_proofreading_reviews(workspace_id,review_id,project_id,editorial_plan_id,themes_pacing_review_id,expected_themes_pacing_revision,expected_themes_pacing_digest,version,rule_set,style_guide,language_tag,actor,snapshot_json,revision,status,decision,decision_reason,expected_repair_revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$plan,$source,4,'themes-digest',1,'copyedit-v1','Chicago-17','en-US','copy-editor','{}',$r,'APPROVED','APPROVE','Approved.',NULL,NULL,$at,$at);";
            r.Parameters.AddWithValue("$w", workspace); r.Parameters.AddWithValue("$id", review.ToString("D")); r.Parameters.AddWithValue("$p", project.ToString("D")); r.Parameters.AddWithValue("$plan", plan.ToString("D")); r.Parameters.AddWithValue("$source", Guid.NewGuid().ToString("D")); r.Parameters.AddWithValue("$r", revision); r.Parameters.AddWithValue("$at", at.ToString("O")); r.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

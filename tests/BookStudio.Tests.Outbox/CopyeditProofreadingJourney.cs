using System.Security.Cryptography;
using System.Text;
using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class CopyeditProofreadingJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "copyedit-proofreading.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 32, "Copyedit/proofreading migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 30, 1, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var project = Guid.NewGuid(); var plan = Guid.NewGuid(); var themesReview = Guid.NewGuid(); const long themesRevision = 5;
        var themesDigest = Hash($"{workspace}:{themesReview:D}:{themesRevision}:APPROVED");
        SeedAuthority(factory, workspace, project, plan, themesReview, themesRevision, now);
        var reviewId = Guid.NewGuid(); Guid approvedMessage;

        await using (var store = new SqliteCopyeditProofreadingStore(factory))
        {
            var draft = new CopyeditProofreadingReviewDraft(reviewId, project, workspace, plan, themesReview, themesRevision, themesDigest, 1, "copyedit-v1", "Chicago-17", "en-US", "copy-editor", "{\"chapters\":[1,2],\"paragraphs\":24}", "create-copyedit-review");
            var created = await store.CreateAsync(draft, now.AddMinutes(1));
            Require(!created.Replayed && created.Review.Status == CopyeditProofreadingReviewStatus.Proposed && created.Review.Revision == 1, "Create failed.");
            Require((await store.CreateAsync(draft, now.AddMinutes(2))).Replayed, "Create replay failed.");
            await Throws<CopyeditProofreadingConflictException>(() => store.CreateAsync(draft with { Actor = "other-editor" }, now.AddMinutes(2)).AsTask());

            var blocking = new[]
            {
                new CopyeditProofreadingFindingDraft(Guid.NewGuid(), CopyeditProofreadingFindingArea.FactualTypo, CopyeditProofreadingSeverity.Blocking, "identity-consistency", "chapter-2/scene-4/p-7", new[] { 2 }, new[] { "scene-4" }, new[] { "p-7" }, new[] { "12-26" }, "Use the verified character surname.", "The surname contradicts the approved manuscript authority.")
            };
            var evaluatedBlocking = await store.EvaluateAsync(new CopyeditProofreadingEvaluateCommand(Guid.NewGuid(), workspace, reviewId, 1, blocking, "blocking copyedit evidence", "copy-editor", "evaluate-blocking-copyedit"), now.AddMinutes(3));
            Require(evaluatedBlocking.Status == CopyeditProofreadingReviewStatus.Evaluated && evaluatedBlocking.Revision == 2, "Blocking evaluation failed.");
            await Throws<CopyeditProofreadingTransitionException>(() => store.DecideAsync(new CopyeditProofreadingDecisionCommand(Guid.NewGuid(), workspace, reviewId, 2, CopyeditProofreadingDecision.Approve, "Approve despite blocker.", null, "copy-editor", "invalid-copyedit-approval"), now.AddMinutes(4)).AsTask());

            var repair = await store.DecideAsync(new CopyeditProofreadingDecisionCommand(Guid.NewGuid(), workspace, reviewId, 2, CopyeditProofreadingDecision.ReturnToRepair, "Correct the blocking factual typo.", 2, "copy-editor", "return-copyedit-repair"), now.AddMinutes(5));
            Require(repair.Status == CopyeditProofreadingReviewStatus.RepairRequired && repair.Revision == 3, "Repair failed.");

            var findings = new[]
            {
                new CopyeditProofreadingFindingDraft(Guid.NewGuid(), CopyeditProofreadingFindingArea.Punctuation, CopyeditProofreadingSeverity.Minor, "serial-comma", "chapter-1/scene-2/p-3", new[] { 1 }, new[] { "scene-2" }, new[] { "p-3" }, new[] { "5-18" }, "Insert the serial comma.", "Style-guide punctuation is consistent after repair.", false),
                new CopyeditProofreadingFindingDraft(Guid.NewGuid(), CopyeditProofreadingFindingArea.TypographicConsistency, CopyeditProofreadingSeverity.Info, "smart-quotes", "chapter-2/scene-4/p-8", new[] { 2 }, new[] { "scene-4" }, new[] { "p-8" }, new[] { "0-42" }, "Normalize quotation marks.", "Typography is normalized across the snapshot.", false)
            };
            var evaluate = new CopyeditProofreadingEvaluateCommand(Guid.NewGuid(), workspace, reviewId, 3, findings, "repaired copyedit evidence", "copy-editor", "evaluate-repaired-copyedit");
            var evaluated = await store.EvaluateAsync(evaluate, now.AddMinutes(6));
            Require(evaluated.Status == CopyeditProofreadingReviewStatus.Evaluated && evaluated.Revision == 4 && evaluated.Findings.Count == 2, "Re-evaluation failed.");
            Require((await store.EvaluateAsync(evaluate, now.AddMinutes(7))).Revision == 4, "Evaluation replay changed state.");

            var decide = new CopyeditProofreadingDecisionCommand(Guid.NewGuid(), workspace, reviewId, 4, CopyeditProofreadingDecision.Approve, "Copyedit and proofreading checks are complete.", null, "copy-editor", "approve-copyedit-review");
            var approved = await store.DecideAsync(decide, now.AddMinutes(8));
            Require(approved.Status == CopyeditProofreadingReviewStatus.Approved && approved.Revision == 5, "Approval failed.");
            approvedMessage = approved.MessageId ?? throw new InvalidOperationException("Approval message missing.");
            Require((await store.DecideAsync(decide, now.AddMinutes(9))).Revision == 5, "Decision replay changed state.");
            Require(await store.GetAsync("workspace-b", reviewId) is null, "Workspace isolation failed.");
        }

        await using (var restarted = new SqliteCopyeditProofreadingStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, reviewId) ?? throw new InvalidOperationException("Review missing after restart.");
            Require(durable.Status == CopyeditProofreadingReviewStatus.Approved && durable.Revision == 5 && durable.Findings.Count == 2, "Restart durability failed.");
        }

        using (var c = factory.OpenConnection())
        {
            using var history = c.CreateCommand();
            history.CommandText = "SELECT COUNT(*) FROM copyedit_proofreading_history WHERE workspace_id=$w AND review_id=$id;";
            history.Parameters.AddWithValue("$w", workspace); history.Parameters.AddWithValue("$id", reviewId.ToString("D"));
            Require(Convert.ToInt32(history.ExecuteScalar()) == 5, "History is not append-only exactly once.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("copyedit-worker", 30, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == approvedMessage && x.EventType == "editorial.copyedit-proofreading.approved") == 1, "Approval event was not exactly once.");
    }

    private static void SeedAuthority(SqliteConnectionFactory factory, string workspace, Guid project, Guid plan, Guid review, long revision, DateTimeOffset at)
    {
        using var c = factory.OpenConnection(); using var tx = c.BeginTransaction();
        using (var p = c.CreateCommand())
        {
            p.Transaction = tx;
            p.CommandText = "INSERT INTO editorial_pass_plans(workspace_id,plan_id,project_id,cross_chapter_audit_id,expected_audit_revision,expected_audit_digest,version,actor,revision,status,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,1,'audit-digest',1,'lead-editor',7,'IN_PROGRESS',NULL,$at,$at);";
            p.Parameters.AddWithValue("$w", workspace); p.Parameters.AddWithValue("$id", plan.ToString("D")); p.Parameters.AddWithValue("$p", project.ToString("D")); p.Parameters.AddWithValue("$a", Guid.NewGuid().ToString("D")); p.Parameters.AddWithValue("$at", at.ToString("O")); p.ExecuteNonQuery();
        }
        using (var n = c.CreateCommand())
        {
            n.Transaction = tx;
            n.CommandText = "INSERT INTO editorial_pass_nodes(workspace_id,plan_id,pass_kind,ordinal,dependencies_json,status,attempts,gate_result,evidence,result,responsible,started_at_utc,completed_at_utc) VALUES($w,$id,'COPYEDITPROOFREADING',5,'[4]','READY',0,NULL,NULL,NULL,NULL,NULL,NULL);";
            n.Parameters.AddWithValue("$w", workspace); n.Parameters.AddWithValue("$id", plan.ToString("D")); n.ExecuteNonQuery();
        }
        using (var r = c.CreateCommand())
        {
            r.Transaction = tx;
            r.CommandText = "INSERT INTO themes_pacing_reviews(workspace_id,review_id,project_id,editorial_plan_id,dialogue_review_id,expected_dialogue_revision,expected_dialogue_digest,version,rule_set,actor,snapshot_json,revision,status,decision,decision_reason,expected_repair_revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$plan,$source,4,'dialogue-digest',1,'themes-v1','themes-editor','{}',$r,'APPROVED','APPROVE','Approved.',NULL,NULL,$at,$at);";
            r.Parameters.AddWithValue("$w", workspace); r.Parameters.AddWithValue("$id", review.ToString("D")); r.Parameters.AddWithValue("$p", project.ToString("D")); r.Parameters.AddWithValue("$plan", plan.ToString("D")); r.Parameters.AddWithValue("$source", Guid.NewGuid().ToString("D")); r.Parameters.AddWithValue("$r", revision); r.Parameters.AddWithValue("$at", at.ToString("O")); r.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

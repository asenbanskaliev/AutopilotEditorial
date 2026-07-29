using System.Security.Cryptography;
using System.Text;
using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class ThemesPacingEditingJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "themes-pacing-editing.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 31, "Themes/pacing editing migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var project = Guid.NewGuid(); var plan = Guid.NewGuid(); var dialogueReview = Guid.NewGuid(); const long dialogueRevision = 5;
        var dialogueDigest = Hash($"{workspace}:{dialogueReview:D}:{dialogueRevision}:APPROVED");
        SeedAuthority(factory, workspace, project, plan, dialogueReview, dialogueRevision, now);
        var reviewId = Guid.NewGuid(); Guid approvedMessage;
        await using (var store = new SqliteThemesPacingEditingStore(factory))
        {
            var draft = new ThemesPacingReviewDraft(reviewId, project, workspace, plan, dialogueReview, dialogueRevision, dialogueDigest, 1, "themes-pacing-v1", "themes-editor", "{\"chapters\":[1,2],\"beats\":12}", "create-themes-pacing-review");
            var created = await store.CreateAsync(draft, now.AddMinutes(1));
            Require(!created.Replayed && created.Review.Status == ThemesPacingReviewStatus.Proposed && created.Review.Revision == 1, "Create failed.");
            Require((await store.CreateAsync(draft, now.AddMinutes(2))).Replayed, "Create replay failed.");
            await Throws<ThemesPacingEditingConflictException>(() => store.CreateAsync(draft with { Actor = "other-editor" }, now.AddMinutes(2)).AsTask());
            var blocking = new[] { new ThemesPacingFindingDraft(Guid.NewGuid(), ThemesPacingFindingArea.ThemePayoff, ThemesPacingSeverity.Blocking, "theme-unresolved", "chapter-2/scene-5", new[] { 2 }, new[] { "scene-5" }, new[] { "beat-12" }, new[] { "0-80" }, "Primary thematic promise has no payoff.") };
            var evaluatedBlocking = await store.EvaluateAsync(new ThemesPacingEvaluateCommand(Guid.NewGuid(), workspace, reviewId, 1, blocking, "blocking themes evidence", "themes-editor", "evaluate-blocking-themes"), now.AddMinutes(3));
            Require(evaluatedBlocking.Status == ThemesPacingReviewStatus.Evaluated && evaluatedBlocking.Revision == 2, "Blocking evaluation failed.");
            await Throws<ThemesPacingEditingTransitionException>(() => store.DecideAsync(new ThemesPacingDecisionCommand(Guid.NewGuid(), workspace, reviewId, 2, ThemesPacingDecision.Approve, "Approve despite blocker.", null, "themes-editor", "invalid-approval"), now.AddMinutes(4)).AsTask());
            var repair = await store.DecideAsync(new ThemesPacingDecisionCommand(Guid.NewGuid(), workspace, reviewId, 2, ThemesPacingDecision.ReturnToRepair, "Add thematic payoff and rebalance pacing.", 2, "themes-editor", "return-themes-repair"), now.AddMinutes(5));
            Require(repair.Status == ThemesPacingReviewStatus.RepairRequired && repair.Revision == 3, "Repair failed.");
            var findings = new[] {
                new ThemesPacingFindingDraft(Guid.NewGuid(), ThemesPacingFindingArea.ThemeProgression, ThemesPacingSeverity.Minor, "theme-progresses", "chapter-1/scene-2", new[] { 1 }, new[] { "scene-2" }, new[] { "beat-4" }, new[] { "0-45" }, "Theme progresses through action.", false),
                new ThemesPacingFindingDraft(Guid.NewGuid(), ThemesPacingFindingArea.Momentum, ThemesPacingSeverity.Info, "momentum-sustained", "chapter-2/scene-5", new[] { 2 }, new[] { "scene-5" }, new[] { "beat-12" }, new[] { "0-80" }, "Escalation and payoff sustain momentum.", false)
            };
            var evaluate = new ThemesPacingEvaluateCommand(Guid.NewGuid(), workspace, reviewId, 3, findings, "repaired themes pacing evidence", "themes-editor", "evaluate-repaired-themes");
            var evaluated = await store.EvaluateAsync(evaluate, now.AddMinutes(6));
            Require(evaluated.Status == ThemesPacingReviewStatus.Evaluated && evaluated.Revision == 4 && evaluated.Findings.Count == 2, "Re-evaluation failed.");
            Require((await store.EvaluateAsync(evaluate, now.AddMinutes(7))).Revision == 4, "Evaluation replay changed state.");
            var decide = new ThemesPacingDecisionCommand(Guid.NewGuid(), workspace, reviewId, 4, ThemesPacingDecision.Approve, "Themes progress and pacing is coherent.", null, "themes-editor", "approve-themes-pacing-review");
            var approved = await store.DecideAsync(decide, now.AddMinutes(8));
            Require(approved.Status == ThemesPacingReviewStatus.Approved && approved.Revision == 5, "Approval failed.");
            approvedMessage = approved.MessageId ?? throw new InvalidOperationException("Approval message missing.");
            Require((await store.DecideAsync(decide, now.AddMinutes(9))).Revision == 5, "Decision replay changed state.");
            Require(await store.GetAsync("workspace-b", reviewId) is null, "Workspace isolation failed.");
        }
        await using (var restarted = new SqliteThemesPacingEditingStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, reviewId) ?? throw new InvalidOperationException("Review missing after restart.");
            Require(durable.Status == ThemesPacingReviewStatus.Approved && durable.Revision == 5 && durable.Findings.Count == 2, "Restart durability failed.");
        }
        using (var c = factory.OpenConnection())
        {
            using var history = c.CreateCommand(); history.CommandText = "SELECT COUNT(*) FROM themes_pacing_history WHERE workspace_id=$w AND review_id=$id;"; history.Parameters.AddWithValue("$w", workspace); history.Parameters.AddWithValue("$id", reviewId.ToString("D")); Require(Convert.ToInt32(history.ExecuteScalar()) == 5, "History is not append-only exactly once.");
        }
        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("themes-worker", 30, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == approvedMessage && x.EventType == "editorial.themes-pacing.approved") == 1, "Approval event was not exactly once.");
    }

    private static void SeedAuthority(SqliteConnectionFactory factory, string workspace, Guid project, Guid plan, Guid review, long revision, DateTimeOffset at)
    {
        using var c = factory.OpenConnection(); using var tx = c.BeginTransaction();
        using (var p = c.CreateCommand()) { p.Transaction = tx; p.CommandText = "INSERT INTO editorial_pass_plans(workspace_id,plan_id,project_id,cross_chapter_audit_id,expected_audit_revision,expected_audit_digest,version,actor,revision,status,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,1,'audit-digest',1,'lead-editor',6,'IN_PROGRESS',NULL,$at,$at);"; p.Parameters.AddWithValue("$w",workspace);p.Parameters.AddWithValue("$id",plan.ToString("D"));p.Parameters.AddWithValue("$p",project.ToString("D"));p.Parameters.AddWithValue("$a",Guid.NewGuid().ToString("D"));p.Parameters.AddWithValue("$at",at.ToString("O"));p.ExecuteNonQuery(); }
        using (var n = c.CreateCommand()) { n.Transaction=tx; n.CommandText="INSERT INTO editorial_pass_nodes(workspace_id,plan_id,pass_kind,ordinal,dependencies_json,status,attempts,gate_result,evidence,result,responsible,started_at_utc,completed_at_utc) VALUES($w,$id,'THEMESPACING',4,'[3]','READY',0,NULL,NULL,NULL,NULL,NULL,NULL);"; n.Parameters.AddWithValue("$w",workspace);n.Parameters.AddWithValue("$id",plan.ToString("D"));n.ExecuteNonQuery(); }
        using (var r = c.CreateCommand()) { r.Transaction=tx; r.CommandText="INSERT INTO dialogue_reviews(workspace_id,review_id,project_id,editorial_plan_id,voice_line_review_id,expected_voice_line_revision,expected_voice_line_digest,version,rule_set,actor,snapshot_json,revision,status,decision,decision_reason,expected_repair_revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$plan,$source,4,'voice-digest',1,'dialogue-v1','dialogue-editor','{}',$r,'APPROVED','APPROVE','Approved.',NULL,NULL,$at,$at);";r.Parameters.AddWithValue("$w",workspace);r.Parameters.AddWithValue("$id",review.ToString("D"));r.Parameters.AddWithValue("$p",project.ToString("D"));r.Parameters.AddWithValue("$plan",plan.ToString("D"));r.Parameters.AddWithValue("$source",Guid.NewGuid().ToString("D"));r.Parameters.AddWithValue("$r",revision);r.Parameters.AddWithValue("$at",at.ToString("O"));r.ExecuteNonQuery(); }
        tx.Commit();
    }
    private static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

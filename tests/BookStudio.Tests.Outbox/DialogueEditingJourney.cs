using System.Security.Cryptography;
using System.Text;
using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class DialogueEditingJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "dialogue-editing.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 30, "Dialogue editing migration missing.");

        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 29, 23, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var project = Guid.NewGuid();
        var plan = Guid.NewGuid();
        var voiceLineReview = Guid.NewGuid();
        const long voiceLineRevision = 5;
        var voiceLineDigest = Hash($"{workspace}:{voiceLineReview:D}:{voiceLineRevision}:APPROVED");
        SeedAuthority(factory, workspace, project, plan, voiceLineReview, voiceLineRevision, now);

        var reviewId = Guid.NewGuid();
        Guid approvedMessage;
        await using (var store = new SqliteDialogueEditingStore(factory))
        {
            var draft = new DialogueReviewDraft(reviewId, project, workspace, plan, voiceLineReview, voiceLineRevision, voiceLineDigest, 1, "dialogue-v1", "dialogue-editor", "{\"chapters\":[1,2],\"exchanges\":8}", "create-dialogue-review");
            var created = await store.CreateAsync(draft, now.AddMinutes(1));
            Require(!created.Replayed && created.Review.Status == DialogueReviewStatus.Proposed && created.Review.Revision == 1, "Create failed.");
            Require((await store.CreateAsync(draft, now.AddMinutes(2))).Replayed, "Create replay failed.");
            await Throws<DialogueEditingConflictException>(() => store.CreateAsync(draft with { Actor = "other-editor" }, now.AddMinutes(2)).AsTask());

            var blocking = new[]
            {
                new DialogueFindingDraft(Guid.NewGuid(), DialogueFindingArea.Attribution, DialogueSeverity.Blocking, "ambiguous-speaker", "chapter-1/scene-2/exchange-4", new[] { 1 }, new[] { "scene-2" }, new[] { "exchange-4" }, new[] { "speaker-a", "speaker-b" }, new[] { "line-11" }, new[] { "0-41" }, "The line cannot be attributed to a unique speaker.")
            };
            var evaluateBlocking = new DialogueEvaluateCommand(Guid.NewGuid(), workspace, reviewId, 1, blocking, "blocking dialogue evidence", "dialogue-editor", "evaluate-blocking-dialogue");
            var evaluatedBlocking = await store.EvaluateAsync(evaluateBlocking, now.AddMinutes(3));
            Require(evaluatedBlocking.Status == DialogueReviewStatus.Evaluated && evaluatedBlocking.Revision == 2, "Blocking evaluation failed.");
            await Throws<DialogueEditingTransitionException>(() => store.DecideAsync(new DialogueDecisionCommand(Guid.NewGuid(), workspace, reviewId, 2, DialogueDecision.Approve, "Approve despite blocker.", null, "dialogue-editor", "invalid-approval"), now.AddMinutes(4)).AsTask());

            var repair = await store.DecideAsync(new DialogueDecisionCommand(Guid.NewGuid(), workspace, reviewId, 2, DialogueDecision.ReturnToRepair, "Resolve speaker attribution.", 2, "dialogue-editor", "return-dialogue-repair"), now.AddMinutes(5));
            Require(repair.Status == DialogueReviewStatus.RepairRequired && repair.Revision == 3, "Repair decision failed.");

            var findings = new[]
            {
                new DialogueFindingDraft(Guid.NewGuid(), DialogueFindingArea.Subtext, DialogueSeverity.Minor, "subtext-present", "chapter-1/scene-2/exchange-4", new[] { 1 }, new[] { "scene-2" }, new[] { "exchange-4" }, new[] { "speaker-a", "speaker-b" }, new[] { "line-11", "line-12" }, new[] { "0-41" }, "Intent is conveyed through implication rather than exposition.", false),
                new DialogueFindingDraft(Guid.NewGuid(), DialogueFindingArea.VoiceDifferentiation, DialogueSeverity.Info, "distinct-voices", "chapter-2/scene-5/exchange-9", new[] { 2 }, new[] { "scene-5" }, new[] { "exchange-9" }, new[] { "speaker-c", "speaker-d" }, new[] { "line-31", "line-32" }, new[] { "0-58" }, "Lexical and syntactic choices distinguish both speakers.", false)
            };
            var evaluate = new DialogueEvaluateCommand(Guid.NewGuid(), workspace, reviewId, 3, findings, "repaired dialogue evaluation evidence", "dialogue-editor", "evaluate-repaired-dialogue");
            var evaluated = await store.EvaluateAsync(evaluate, now.AddMinutes(6));
            Require(evaluated.Status == DialogueReviewStatus.Evaluated && evaluated.Revision == 4 && evaluated.Findings.Count == 2, "Re-evaluation failed.");
            Require((await store.EvaluateAsync(evaluate, now.AddMinutes(7))).Revision == 4, "Evaluation replay changed state.");

            var decide = new DialogueDecisionCommand(Guid.NewGuid(), workspace, reviewId, 4, DialogueDecision.Approve, "Dialogue subtext, attribution and voice differentiation are coherent.", null, "dialogue-editor", "approve-dialogue-review");
            var approved = await store.DecideAsync(decide, now.AddMinutes(8));
            Require(approved.Status == DialogueReviewStatus.Approved && approved.Revision == 5 && approved.Decision == DialogueDecision.Approve, "Approval failed.");
            approvedMessage = approved.MessageId ?? throw new InvalidOperationException("Approval message missing.");
            Require((await store.DecideAsync(decide, now.AddMinutes(9))).Revision == 5, "Decision replay changed state.");
            Require(await store.GetAsync("workspace-b", reviewId) is null, "Workspace isolation failed.");
        }

        await using (var restarted = new SqliteDialogueEditingStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, reviewId) ?? throw new InvalidOperationException("Review missing after restart.");
            Require(durable.Status == DialogueReviewStatus.Approved && durable.Revision == 5 && durable.Findings.Count == 2, "Restart durability failed.");
        }

        using (var c = factory.OpenConnection())
        {
            using var history = c.CreateCommand();
            history.CommandText = "SELECT COUNT(*) FROM dialogue_history WHERE workspace_id=$w AND review_id=$id;";
            history.Parameters.AddWithValue("$w", workspace);
            history.Parameters.AddWithValue("$id", reviewId.ToString("D"));
            Require(Convert.ToInt32(history.ExecuteScalar()) == 5, "History is not append-only exactly once.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("dialogue-worker", 30, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == approvedMessage && x.EventType == "editorial.dialogue.approved") == 1, "Approval event was not exactly once.");
    }

    private static void SeedAuthority(SqliteConnectionFactory factory, string workspace, Guid project, Guid plan, Guid review, long revision, DateTimeOffset at)
    {
        using var c = factory.OpenConnection();
        using var tx = c.BeginTransaction();
        using (var p = c.CreateCommand())
        {
            p.Transaction = tx;
            p.CommandText = "INSERT INTO editorial_pass_plans(workspace_id,plan_id,project_id,cross_chapter_audit_id,expected_audit_revision,expected_audit_digest,version,actor,revision,status,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,1,'audit-digest',1,'lead-editor',5,'IN_PROGRESS',NULL,$at,$at);";
            p.Parameters.AddWithValue("$w", workspace); p.Parameters.AddWithValue("$id", plan.ToString("D")); p.Parameters.AddWithValue("$p", project.ToString("D")); p.Parameters.AddWithValue("$a", Guid.NewGuid().ToString("D")); p.Parameters.AddWithValue("$at", at.ToString("O"));
            p.ExecuteNonQuery();
        }
        using (var n = c.CreateCommand())
        {
            n.Transaction = tx;
            n.CommandText = "INSERT INTO editorial_pass_nodes(workspace_id,plan_id,pass_kind,ordinal,dependencies_json,status,attempts,gate_result,evidence,result,responsible,started_at_utc,completed_at_utc) VALUES($w,$id,'DIALOGUE',3,'[2]','READY',0,NULL,NULL,NULL,NULL,NULL,NULL);";
            n.Parameters.AddWithValue("$w", workspace); n.Parameters.AddWithValue("$id", plan.ToString("D"));
            n.ExecuteNonQuery();
        }
        using (var r = c.CreateCommand())
        {
            r.Transaction = tx;
            r.CommandText = "INSERT INTO voice_line_reviews(workspace_id,review_id,project_id,editorial_plan_id,structural_content_review_id,expected_structural_content_revision,expected_structural_content_digest,version,rule_set,actor,snapshot_json,revision,status,decision,decision_reason,expected_repair_revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$plan,$source,3,'structural-digest',1,'voice-line-v1','line-editor','{}',$r,'APPROVED','APPROVE','Approved.',NULL,NULL,$at,$at);";
            r.Parameters.AddWithValue("$w", workspace); r.Parameters.AddWithValue("$id", review.ToString("D")); r.Parameters.AddWithValue("$p", project.ToString("D")); r.Parameters.AddWithValue("$plan", plan.ToString("D")); r.Parameters.AddWithValue("$source", Guid.NewGuid().ToString("D")); r.Parameters.AddWithValue("$r", revision); r.Parameters.AddWithValue("$at", at.ToString("O"));
            r.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}

using System.Security.Cryptography;
using System.Text;
using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class VoiceLineEditingJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "voice-line-editing.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 29, "Voice line editing migration missing.");

        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 29, 22, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var project = Guid.NewGuid();
        var plan = Guid.NewGuid();
        var structuralReview = Guid.NewGuid();
        const long structuralRevision = 3;
        var structuralDigest = Hash($"{workspace}:{structuralReview:D}:{structuralRevision}:APPROVED");
        SeedAuthority(factory, workspace, project, plan, structuralReview, structuralRevision, now);

        var reviewId = Guid.NewGuid();
        Guid approvedMessage;
        await using (var store = new SqliteVoiceLineEditingStore(factory))
        {
            var draft = new VoiceLineReviewDraft(reviewId, project, workspace, plan, structuralReview, structuralRevision, structuralDigest, 1, "voice-line-v1", "line-editor", "{\"chapters\":[1,2],\"paragraphs\":12}", "create-voice-line-review");
            var created = await store.CreateAsync(draft, now.AddMinutes(1));
            Require(!created.Replayed && created.Review.Status == VoiceLineReviewStatus.Proposed && created.Review.Revision == 1, "Create failed.");
            Require((await store.CreateAsync(draft, now.AddMinutes(2))).Replayed, "Create replay failed.");
            await Throws<VoiceLineEditingConflictException>(() => store.CreateAsync(draft with { Actor = "other-editor" }, now.AddMinutes(2)).AsTask());

            var blocking = new[]
            {
                new VoiceLineFindingDraft(Guid.NewGuid(), VoiceLineFindingArea.SentenceClarity, VoiceLineSeverity.Blocking, "ambiguous-reference", "chapter-1/scene-2/paragraph-4", new[] { 1 }, new[] { "scene-2" }, new[] { "p-4" }, new[] { "12-38" }, "The pronoun has two plausible antecedents.")
            };
            var evaluateBlocking = new VoiceLineEvaluateCommand(Guid.NewGuid(), workspace, reviewId, 1, blocking, "blocking voice-line evidence", "line-editor", "evaluate-blocking-voice-line");
            var evaluatedBlocking = await store.EvaluateAsync(evaluateBlocking, now.AddMinutes(3));
            Require(evaluatedBlocking.Status == VoiceLineReviewStatus.Evaluated && evaluatedBlocking.Revision == 2, "Blocking evaluation failed.");
            await Throws<VoiceLineEditingTransitionException>(() => store.DecideAsync(new VoiceLineDecisionCommand(Guid.NewGuid(), workspace, reviewId, 2, VoiceLineDecision.Approve, "Approve despite blocker.", null, "line-editor", "invalid-approval"), now.AddMinutes(4)).AsTask());

            var repair = await store.DecideAsync(new VoiceLineDecisionCommand(Guid.NewGuid(), workspace, reviewId, 2, VoiceLineDecision.ReturnToRepair, "Resolve ambiguous reference.", 2, "line-editor", "return-voice-line-repair"), now.AddMinutes(5));
            Require(repair.Status == VoiceLineReviewStatus.RepairRequired && repair.Revision == 3, "Repair decision failed.");

            var findings = new[]
            {
                new VoiceLineFindingDraft(Guid.NewGuid(), VoiceLineFindingArea.NarrativeVoice, VoiceLineSeverity.Minor, "voice-consistency", "chapter-1", new[] { 1 }, new[] { "scene-2" }, new[] { "p-4" }, new[] { "12-38" }, "Narrative distance remains consistent after repair.", false),
                new VoiceLineFindingDraft(Guid.NewGuid(), VoiceLineFindingArea.Rhythm, VoiceLineSeverity.Info, "sentence-variety", "chapter-2", new[] { 2 }, new[] { "scene-5" }, new[] { "p-19" }, new[] { "0-52" }, "Sentence length variation supports the intended pace.", false)
            };
            var evaluate = new VoiceLineEvaluateCommand(Guid.NewGuid(), workspace, reviewId, 3, findings, "repaired voice-line evaluation evidence", "line-editor", "evaluate-repaired-voice-line");
            var evaluated = await store.EvaluateAsync(evaluate, now.AddMinutes(6));
            Require(evaluated.Status == VoiceLineReviewStatus.Evaluated && evaluated.Revision == 4 && evaluated.Findings.Count == 2, "Re-evaluation failed.");
            Require((await store.EvaluateAsync(evaluate, now.AddMinutes(7))).Revision == 4, "Evaluation replay changed state.");

            var decide = new VoiceLineDecisionCommand(Guid.NewGuid(), workspace, reviewId, 4, VoiceLineDecision.Approve, "Voice, clarity, rhythm and style are coherent.", null, "line-editor", "approve-voice-line-review");
            var approved = await store.DecideAsync(decide, now.AddMinutes(8));
            Require(approved.Status == VoiceLineReviewStatus.Approved && approved.Revision == 5 && approved.Decision == VoiceLineDecision.Approve, "Approval failed.");
            approvedMessage = approved.MessageId ?? throw new InvalidOperationException("Approval message missing.");
            Require((await store.DecideAsync(decide, now.AddMinutes(9))).Revision == 5, "Decision replay changed state.");
            Require(await store.GetAsync("workspace-b", reviewId) is null, "Workspace isolation failed.");
        }

        await using (var restarted = new SqliteVoiceLineEditingStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, reviewId) ?? throw new InvalidOperationException("Review missing after restart.");
            Require(durable.Status == VoiceLineReviewStatus.Approved && durable.Revision == 5 && durable.Findings.Count == 2, "Restart durability failed.");
        }

        using (var c = factory.OpenConnection())
        {
            using var history = c.CreateCommand();
            history.CommandText = "SELECT COUNT(*) FROM voice_line_history WHERE workspace_id=$w AND review_id=$id;";
            history.Parameters.AddWithValue("$w", workspace);
            history.Parameters.AddWithValue("$id", reviewId.ToString("D"));
            Require(Convert.ToInt32(history.ExecuteScalar()) == 5, "History is not append-only exactly once.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("voice-line-worker", 30, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == approvedMessage && x.EventType == "editorial.voice-line.approved") == 1, "Approval event was not exactly once.");
    }

    private static void SeedAuthority(SqliteConnectionFactory factory, string workspace, Guid project, Guid plan, Guid review, long revision, DateTimeOffset at)
    {
        using var c = factory.OpenConnection();
        using var tx = c.BeginTransaction();
        using (var p = c.CreateCommand())
        {
            p.Transaction = tx;
            p.CommandText = "INSERT INTO editorial_pass_plans(workspace_id,plan_id,project_id,cross_chapter_audit_id,expected_audit_revision,expected_audit_digest,version,actor,revision,status,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,1,'audit-digest',1,'lead-editor',4,'IN_PROGRESS',NULL,$at,$at);";
            p.Parameters.AddWithValue("$w", workspace); p.Parameters.AddWithValue("$id", plan.ToString("D")); p.Parameters.AddWithValue("$p", project.ToString("D")); p.Parameters.AddWithValue("$a", Guid.NewGuid().ToString("D")); p.Parameters.AddWithValue("$at", at.ToString("O"));
            p.ExecuteNonQuery();
        }
        using (var n = c.CreateCommand())
        {
            n.Transaction = tx;
            n.CommandText = "INSERT INTO editorial_pass_nodes(workspace_id,plan_id,pass_kind,ordinal,dependencies_json,status,attempts,gate_result,evidence,result,responsible,started_at_utc,completed_at_utc) VALUES($w,$id,'VOICELINE',2,'[1]','READY',0,NULL,NULL,NULL,NULL,NULL,NULL);";
            n.Parameters.AddWithValue("$w", workspace); n.Parameters.AddWithValue("$id", plan.ToString("D"));
            n.ExecuteNonQuery();
        }
        using (var r = c.CreateCommand())
        {
            r.Transaction = tx;
            r.CommandText = "INSERT INTO structural_content_reviews(workspace_id,review_id,project_id,editorial_plan_id,developmental_review_id,expected_developmental_revision,expected_developmental_digest,version,rule_set,actor,snapshot_json,revision,status,decision,decision_reason,expected_repair_revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$plan,$dev,3,'developmental-digest',1,'structural-v1','lead-editor','{}',$r,'APPROVED','APPROVE','Approved.',NULL,NULL,$at,$at);";
            r.Parameters.AddWithValue("$w", workspace); r.Parameters.AddWithValue("$id", review.ToString("D")); r.Parameters.AddWithValue("$p", project.ToString("D")); r.Parameters.AddWithValue("$plan", plan.ToString("D")); r.Parameters.AddWithValue("$dev", Guid.NewGuid().ToString("D")); r.Parameters.AddWithValue("$r", revision); r.Parameters.AddWithValue("$at", at.ToString("O"));
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

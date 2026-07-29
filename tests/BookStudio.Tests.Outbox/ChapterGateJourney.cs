using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class ChapterGateJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "chapter-gate.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 23, "Chapter gate migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 29, 5, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var project = Guid.NewGuid();
        var gateId = Guid.NewGuid();
        SeedChapter(factory, workspace, "chapter-01", 3, "digest-03", now);
        SeedClosedAudit(factory, workspace, project, now);

        Guid lockedMessage;
        Guid reopenedMessage;
        await using (var store = new SqliteChapterGateStore(factory))
        {
            var draft = new ChapterGateDraft(gateId, project, workspace, "chapter-01", 3, "digest-03", "auditor", "create-gate");
            var created = await store.CreateAsync(draft, now.AddMinutes(1));
            Require(!created.Replayed && created.Gate.Status == ChapterGateStatus.Proposed, "Create failed.");
            Require((await store.CreateAsync(draft, now.AddMinutes(2))).Replayed, "Create replay failed.");
            await Throws<ChapterGateConflictException>(() => store.CreateAsync(draft with { Actor = "other" }, now.AddMinutes(2)).AsTask());

            var evaluate = new ChapterGateControlCommand(Guid.NewGuid(), workspace, gateId, 1, "auditor", "evaluate");
            var evaluated = await store.EvaluateAsync(evaluate, now.AddMinutes(3));
            Require(evaluated.Status == ChapterGateStatus.Evaluated && evaluated.Revision == 2 && evaluated.Findings.Count == 0, "Evaluation failed.");
            Require((await store.EvaluateAsync(evaluate, now.AddMinutes(4))).Revision == 2, "Evaluation replay changed state.");

            var decide = new ChapterGateDecisionCommand(Guid.NewGuid(), workspace, gateId, 2, ChapterGateDecision.Approve, "All cumulative gates pass.", "editor", "approve");
            var locked = await store.DecideAsync(decide, now.AddMinutes(5));
            lockedMessage = locked.MessageId ?? throw new InvalidOperationException("Lock message missing.");
            Require(locked.Status == ChapterGateStatus.Locked && locked.Revision == 3, "Lock failed.");
            Require((await store.DecideAsync(decide, now.AddMinutes(6))).Revision == 3, "Decision replay changed state.");
            await Throws<ChapterGateTransitionException>(() => store.EvaluateAsync(new(Guid.NewGuid(), workspace, gateId, 3, "editor", "locked-mutation"), now.AddMinutes(7)).AsTask());

            var reopen = new ChapterGateReopenCommand(Guid.NewGuid(), workspace, gateId, 3, "Authorized repair required.", "editor", "reopen");
            var reopened = await store.ReopenAsync(reopen, now.AddMinutes(8));
            reopenedMessage = reopened.MessageId ?? throw new InvalidOperationException("Reopen message missing.");
            Require(reopened.Status == ChapterGateStatus.Reopened && reopened.Revision == 4, "Reopen failed.");
            Require((await store.ReopenAsync(reopen, now.AddMinutes(9))).Revision == 4, "Reopen replay changed state.");
            Require(await store.GetAsync("workspace-b", gateId) is null, "Workspace isolation failed.");
        }

        await using (var restarted = new SqliteChapterGateStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, gateId) ?? throw new InvalidOperationException("Gate missing after restart.");
            Require(durable.Status == ChapterGateStatus.Reopened && durable.Revision == 4, "Restart durability failed.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("chapter-gate-worker", 100, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == lockedMessage && x.EventType == "editorial.chapter-gate.locked") == 1, "Lock event was not exactly once.");
        Require(messages.Count(x => x.MessageId == reopenedMessage && x.EventType == "editorial.chapter-gate.reopened") == 1, "Reopen event was not exactly once.");
    }

    private static void SeedChapter(SqliteConnectionFactory factory, string workspace, string chapterId, int version, string digest, DateTimeOffset at)
    {
        using var c = factory.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO repair_patch_targets(workspace_id,artifact_id,version,digest,content_json,updated_at_utc) VALUES($w,$a,$v,$d,'{}',$at);";
        cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$a", chapterId); cmd.Parameters.AddWithValue("$v", version); cmd.Parameters.AddWithValue("$d", digest); cmd.Parameters.AddWithValue("$at", at.ToString("O")); cmd.ExecuteNonQuery();
    }

    private static void SeedClosedAudit(SqliteConnectionFactory factory, string workspace, Guid project, DateTimeOffset at)
    {
        using var c = factory.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO transition_audits(workspace_id,audit_id,project_id,scope,source_json,target_json,rule_set_version,assessments_json,findings_json,revision,status,closed_message_id,created_at_utc,updated_at_utc) VALUES($w,$a,$p,'CHAPTER','{}','{}','1.0','[]','[]',2,'CLOSED',$m,$at,$at);";
        cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$a", Guid.NewGuid().ToString("D")); cmd.Parameters.AddWithValue("$p", project.ToString("D")); cmd.Parameters.AddWithValue("$m", Guid.NewGuid().ToString("D")); cmd.Parameters.AddWithValue("$at", at.ToString("O")); cmd.ExecuteNonQuery();
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
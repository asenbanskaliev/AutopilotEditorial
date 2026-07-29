using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class MemoryCommitJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "memory-commit.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 24, "Memory commit migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 29, 7, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var project = Guid.NewGuid();
        var gate = Guid.NewGuid();
        var delta = Guid.NewGuid();
        SeedLock(factory, workspace, project, "chapter-01", gate, 4, "digest-04", now);
        SeedProjection(factory, workspace, project, "chapter-01", "KNOWLEDGE", "fact-1", "{\"value\":\"old\"}", "old-digest", now);

        Guid commitMessage;
        await using (var store = new SqliteMemoryCommitStore(factory))
        {
            var entries = new[]
            {
                new MemoryDeltaEntry("KNOWLEDGE", "fact-1", "UPSERT", "old-digest", "{\"value\":\"new\"}"),
                new MemoryDeltaEntry("STATE", "character-1", "UPSERT", "", "{\"mood\":\"resolved\"}")
            };
            var draft = new MemoryDeltaDraft(delta, project, workspace, "chapter-01", gate, 4, "digest-04", entries, "locked chapter snapshot", "editor", "propose-memory");
            var created = await store.ProposeAsync(draft, now.AddMinutes(1));
            Require(!created.Replayed && created.Delta.Status == MemoryDeltaStatus.Proposed, "Proposal failed.");
            Require((await store.ProposeAsync(draft, now.AddMinutes(2))).Replayed, "Proposal replay failed.");
            await Throws<MemoryDeltaConflictException>(() => store.ProposeAsync(draft with { Actor = "other" }, now.AddMinutes(2)).AsTask());

            var validate = new MemoryDeltaControlCommand(Guid.NewGuid(), workspace, delta, 1, "editor", "validate-memory");
            var validated = await store.ValidateAsync(validate, now.AddMinutes(3));
            Require(validated.Status == MemoryDeltaStatus.Validated && validated.Revision == 2, "Validation failed.");
            Require((await store.ValidateAsync(validate, now.AddMinutes(4))).Revision == 2, "Validation replay changed state.");

            var commit = new MemoryDeltaControlCommand(Guid.NewGuid(), workspace, delta, 2, "editor", "commit-memory");
            var committed = await store.CommitAsync(commit, now.AddMinutes(5));
            commitMessage = committed.MessageId ?? throw new InvalidOperationException("Commit message missing.");
            Require(committed.Status == MemoryDeltaStatus.Committed && committed.Revision == 3, "Commit failed.");
            Require((await store.CommitAsync(commit, now.AddMinutes(6))).Revision == 3, "Commit replay changed state.");
            await Throws<MemoryDeltaTransitionException>(() => store.ValidateAsync(new(Guid.NewGuid(), workspace, delta, 3, "editor", "mutate-terminal"), now.AddMinutes(7)).AsTask());
            Require(await store.GetAsync("workspace-b", delta) is null, "Workspace isolation failed.");
        }

        await using (var restarted = new SqliteMemoryCommitStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, delta) ?? throw new InvalidOperationException("Delta missing after restart.");
            Require(durable.Status == MemoryDeltaStatus.Committed && durable.Revision == 3, "Restart durability failed.");
        }

        using (var c = factory.OpenConnection())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM memory_projection_entries WHERE workspace_id=$w AND source_delta_id=$d;";
            cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$d", delta.ToString("D"));
            Require(Convert.ToInt32(cmd.ExecuteScalar()) == 2, "Atomic projection application failed.");
            using var history = c.CreateCommand();
            history.CommandText = "SELECT COUNT(*) FROM memory_delta_history WHERE workspace_id=$w AND delta_id=$d AND status='COMMITTED';";
            history.Parameters.AddWithValue("$w", workspace); history.Parameters.AddWithValue("$d", delta.ToString("D"));
            Require(Convert.ToInt32(history.ExecuteScalar()) == 1, "History was not append-only exactly once.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("memory-worker", 100, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == commitMessage && x.EventType == "editorial.memory-delta.committed") == 1, "Commit event was not exactly once.");
    }

    private static void SeedLock(SqliteConnectionFactory factory, string workspace, Guid project, string chapter, Guid gate, int version, string digest, DateTimeOffset at)
    {
        using var c = factory.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO chapter_gate_locks(workspace_id,chapter_id,gate_id,project_id,locked_version,locked_digest,locked_at_utc,reopened_at_utc) VALUES($w,$c,$g,$p,$v,$d,$at,NULL);";
        cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$c", chapter); cmd.Parameters.AddWithValue("$g", gate.ToString("D")); cmd.Parameters.AddWithValue("$p", project.ToString("D")); cmd.Parameters.AddWithValue("$v", version); cmd.Parameters.AddWithValue("$d", digest); cmd.Parameters.AddWithValue("$at", at.ToString("O")); cmd.ExecuteNonQuery();
    }

    private static void SeedProjection(SqliteConnectionFactory factory, string workspace, Guid project, string chapter, string projection, string entity, string payload, string digest, DateTimeOffset at)
    {
        using var c = factory.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO memory_projection_entries(workspace_id,project_id,chapter_id,projection,entity_id,payload_json,digest,source_delta_id,revision,updated_at_utc) VALUES($w,$p,$c,$pr,$e,$j,$d,$s,1,$at);";
        cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$p", project.ToString("D")); cmd.Parameters.AddWithValue("$c", chapter); cmd.Parameters.AddWithValue("$pr", projection); cmd.Parameters.AddWithValue("$e", entity); cmd.Parameters.AddWithValue("$j", payload); cmd.Parameters.AddWithValue("$d", digest); cmd.Parameters.AddWithValue("$s", Guid.NewGuid().ToString("D")); cmd.Parameters.AddWithValue("$at", at.ToString("O")); cmd.ExecuteNonQuery();
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

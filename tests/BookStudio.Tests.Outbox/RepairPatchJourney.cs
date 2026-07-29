using System.Security.Cryptography;
using System.Text;
using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class RepairPatchJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "repair-patches.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 22, "Repair patch migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 29, 4, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var project = Guid.NewGuid(); var audit = Guid.NewGuid();
        const string artifact = "scene-01";
        const string original = "{\"scene\":{\"location\":\"vault\",\"holder\":\"Mara\"},\"untouched\":\"keep\"}";
        var digest = Hash(original);
        Seed(factory, workspace, project, audit, artifact, 3, digest, original, now);

        var patchId = Guid.NewGuid();
        var operation = new RepairOperation("/scene/holder", RepairOperationKind.ReplaceValue, "Mara", "Nora");
        var draft = new RepairPatchDraft(patchId, project, workspace, artifact, 3, digest, "/scene", [operation], "Correct holder continuity", "closed transition audit", "AUDIT", audit, "editor", "propose-1");
        Guid appliedMessage;
        await using (var store = new SqliteRepairPatchStore(factory))
        {
            var proposed = await store.ProposeAsync(draft, now.AddMinutes(1));
            Require(!proposed.Replayed && proposed.Patch.Status == RepairPatchStatus.Proposed, "Proposal failed.");
            Require((await store.ProposeAsync(draft, now.AddMinutes(2))).Replayed, "Proposal replay failed.");
            await Throws<RepairPatchConflictException>(() => store.ProposeAsync(draft with { Reason = "different" }, now.AddMinutes(2)).AsTask());

            var validate = new RepairPatchControlCommand(Guid.NewGuid(), workspace, patchId, 1, "reviewer", "validate-1");
            var validated = await store.ValidateAsync(validate, now.AddMinutes(3));
            Require(validated.Status == RepairPatchStatus.Validated && validated.Revision == 2, "Validation failed.");
            Require((await store.ValidateAsync(validate, now.AddMinutes(4))).Revision == 2, "Validation replay changed revision.");

            var apply = new RepairPatchControlCommand(Guid.NewGuid(), workspace, patchId, 2, "editor", "apply-1");
            var applied = await store.ApplyAsync(apply, now.AddMinutes(5));
            appliedMessage = applied.MessageId ?? throw new InvalidOperationException("Apply message missing.");
            Require(applied.Status == RepairPatchStatus.Applied && applied.ResultVersion == 4 && applied.ResultDigest != digest, "Apply failed.");
            Require((await store.ApplyAsync(apply, now.AddMinutes(6))).Revision == 3, "Apply replay changed revision.");
            Require(await store.GetAsync("workspace-b", patchId) is null, "Workspace isolation failed.");
        }

        using (var c = factory.OpenConnection())
        {
            using var target = c.CreateCommand();
            target.CommandText = "SELECT version,digest,content_json FROM repair_patch_targets WHERE workspace_id=$w AND artifact_id=$a;";
            target.Parameters.AddWithValue("$w", workspace); target.Parameters.AddWithValue("$a", artifact);
            using var r = target.ExecuteReader(); Require(r.Read() && r.GetInt32(0) == 4 && r.GetString(2).Contains("Nora", StringComparison.Ordinal) && r.GetString(2).Contains("untouched", StringComparison.Ordinal), "Localized target update failed.");
            using var history = c.CreateCommand(); history.CommandText = "SELECT COUNT(*) FROM repair_patch_history WHERE workspace_id=$w AND patch_id=$p AND content_json=$c;"; history.Parameters.AddWithValue("$w", workspace); history.Parameters.AddWithValue("$p", patchId.ToString("D")); history.Parameters.AddWithValue("$c", original); Require(Convert.ToInt64(history.ExecuteScalar()) == 1, "Previous version is not recoverable.");
        }

        await using (var restarted = new SqliteRepairPatchStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, patchId) ?? throw new InvalidOperationException("Patch missing after restart.");
            Require(durable.Status == RepairPatchStatus.Applied && durable.ResultVersion == 4, "Restart durability failed.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("repair-worker", 100, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == appliedMessage && x.EventType == "editorial.repair-patch.applied") == 1, "Apply Outbox event was not exactly once.");
    }

    private static void Seed(SqliteConnectionFactory factory, string workspace, Guid project, Guid audit, string artifact, int version, string digest, string content, DateTimeOffset at)
    {
        using var c = factory.OpenConnection(); using var tx = c.BeginTransaction();
        using (var a = c.CreateCommand()) { a.Transaction = tx; a.CommandText = "INSERT INTO transition_audits(workspace_id,audit_id,project_id,scope,source_json,target_json,rule_set_version,assessments_json,findings_json,revision,status,closed_message_id,created_at_utc,updated_at_utc) VALUES($w,$a,$p,'SCENE','{}','{}','1.0','[]','[]',10,'CLOSED',$m,$at,$at);"; a.Parameters.AddWithValue("$w", workspace); a.Parameters.AddWithValue("$a", audit.ToString("D")); a.Parameters.AddWithValue("$p", project.ToString("D")); a.Parameters.AddWithValue("$m", Guid.NewGuid().ToString("D")); a.Parameters.AddWithValue("$at", at.ToString("O")); a.ExecuteNonQuery(); }
        using (var t = c.CreateCommand()) { t.Transaction = tx; t.CommandText = "INSERT INTO repair_patch_targets(workspace_id,artifact_id,version,digest,content_json,updated_at_utc) VALUES($w,$a,$v,$d,$c,$at);"; t.Parameters.AddWithValue("$w", workspace); t.Parameters.AddWithValue("$a", artifact); t.Parameters.AddWithValue("$v", version); t.Parameters.AddWithValue("$d", digest); t.Parameters.AddWithValue("$c", content); t.Parameters.AddWithValue("$at", at.ToString("O")); t.ExecuteNonQuery(); }
        tx.Commit();
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

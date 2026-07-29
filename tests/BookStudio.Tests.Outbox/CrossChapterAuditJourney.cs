using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class CrossChapterAuditJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "cross-chapter-audit.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 25, "Cross chapter audit migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 29, 9, 0, 0, TimeSpan.Zero);
        const string workspace = "workspace-a";
        var project = Guid.NewGuid();
        var chapter1Gate = Guid.NewGuid();
        var chapter2Gate = Guid.NewGuid();
        var chapter1Memory = Guid.NewGuid();
        var chapter2Memory = Guid.NewGuid();
        SeedSnapshot(factory, workspace, project, "chapter-01", chapter1Gate, chapter1Memory, 4, "lock-01", "memory-01", "fact-1", "digest-a", now);
        SeedSnapshot(factory, workspace, project, "chapter-02", chapter2Gate, chapter2Memory, 5, "lock-02", "memory-02", "fact-2", "digest-b", now);

        var auditId = Guid.NewGuid();
        Guid approvedMessage;
        await using (var store = new SqliteCrossChapterAuditStore(factory))
        {
            var chapters = new[]
            {
                new CrossChapterSnapshotItem("chapter-01", chapter1Gate, 4, "lock-01", chapter1Memory, "memory-01"),
                new CrossChapterSnapshotItem("chapter-02", chapter2Gate, 5, "lock-02", chapter2Memory, "memory-02")
            };
            var draft = new CrossChapterAuditDraft(auditId, project, workspace, "global-v1", chapters, "editor", "locked chapters and committed memory", "create-global-audit");
            var created = await store.CreateAsync(draft, now.AddMinutes(1));
            Require(!created.Replayed && created.Audit.Status == CrossChapterAuditStatus.Proposed, "Create failed.");
            Require((await store.CreateAsync(draft, now.AddMinutes(2))).Replayed, "Create replay failed.");
            await Throws<CrossChapterAuditConflictException>(() => store.CreateAsync(draft with { Actor = "other" }, now.AddMinutes(2)).AsTask());

            var evaluate = new CrossChapterAuditControlCommand(Guid.NewGuid(), workspace, auditId, 1, "editor", "evaluate-global-audit");
            var evaluated = await store.EvaluateAsync(evaluate, now.AddMinutes(3));
            Require(evaluated.Status == CrossChapterAuditStatus.Evaluated && evaluated.Revision == 2, "Evaluate failed.");
            Require(evaluated.Findings.All(x => x.Severity != CrossChapterAuditSeverity.Blocking), "Unexpected blocking continuity finding.");
            Require((await store.EvaluateAsync(evaluate, now.AddMinutes(4))).Revision == 2, "Evaluate replay changed state.");

            var decide = new CrossChapterAuditDecisionCommand(Guid.NewGuid(), workspace, auditId, 2, CrossChapterAuditDecision.Approve, "global continuity reviewed", "editor", "approve-global-audit");
            var approved = await store.DecideAsync(decide, now.AddMinutes(5));
            approvedMessage = approved.MessageId ?? throw new InvalidOperationException("Approval message missing.");
            Require(approved.Status == CrossChapterAuditStatus.Approved && approved.Revision == 3, "Approval failed.");
            Require((await store.DecideAsync(decide, now.AddMinutes(6))).Revision == 3, "Decision replay changed state.");
            Require(await store.GetAsync("workspace-b", auditId) is null, "Workspace isolation failed.");
        }

        await using (var restarted = new SqliteCrossChapterAuditStore(factory))
        {
            var durable = await restarted.GetAsync(workspace, auditId) ?? throw new InvalidOperationException("Audit missing after restart.");
            Require(durable.Status == CrossChapterAuditStatus.Approved && durable.Revision == 3, "Restart durability failed.");
        }

        using (var c = factory.OpenConnection())
        {
            using var history = c.CreateCommand();
            history.CommandText = "SELECT COUNT(*) FROM cross_chapter_audit_history WHERE workspace_id=$w AND audit_id=$id AND status='APPROVED';";
            history.Parameters.AddWithValue("$w", workspace);
            history.Parameters.AddWithValue("$id", auditId.ToString("D"));
            Require(Convert.ToInt32(history.ExecuteScalar()) == 1, "Approval history was not append-only exactly once.");
        }

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("cross-chapter-worker", 100, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count(x => x.MessageId == approvedMessage && x.EventType == "editorial.cross-chapter-audit.approved") == 1, "Approval event was not exactly once.");
    }

    private static void SeedSnapshot(SqliteConnectionFactory factory, string workspace, Guid project, string chapter, Guid gate, Guid memory, int version, string lockDigest, string memoryDigest, string entity, string projectionDigest, DateTimeOffset at)
    {
        using var c = factory.OpenConnection();
        using var tx = c.BeginTransaction();
        using (var lockCommand = c.CreateCommand())
        {
            lockCommand.Transaction = tx;
            lockCommand.CommandText = "INSERT INTO chapter_gate_locks(workspace_id,chapter_id,gate_id,project_id,locked_version,locked_digest,locked_at_utc,reopened_at_utc) VALUES($w,$c,$g,$p,$v,$d,$at,NULL);";
            lockCommand.Parameters.AddWithValue("$w", workspace); lockCommand.Parameters.AddWithValue("$c", chapter); lockCommand.Parameters.AddWithValue("$g", gate.ToString("D")); lockCommand.Parameters.AddWithValue("$p", project.ToString("D")); lockCommand.Parameters.AddWithValue("$v", version); lockCommand.Parameters.AddWithValue("$d", lockDigest); lockCommand.Parameters.AddWithValue("$at", at.ToString("O")); lockCommand.ExecuteNonQuery();
        }
        using (var memoryCommand = c.CreateCommand())
        {
            memoryCommand.Transaction = tx;
            memoryCommand.CommandText = "INSERT INTO memory_deltas(workspace_id,delta_id,project_id,chapter_id,gate_id,locked_version,locked_digest,entries_json,evidence,actor,payload_hash,revision,status,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$c,$g,$v,$ld,'[]','seed','editor',$md,3,'COMMITTED',NULL,$at,$at);";
            memoryCommand.Parameters.AddWithValue("$w", workspace); memoryCommand.Parameters.AddWithValue("$id", memory.ToString("D")); memoryCommand.Parameters.AddWithValue("$p", project.ToString("D")); memoryCommand.Parameters.AddWithValue("$c", chapter); memoryCommand.Parameters.AddWithValue("$g", gate.ToString("D")); memoryCommand.Parameters.AddWithValue("$v", version); memoryCommand.Parameters.AddWithValue("$ld", lockDigest); memoryCommand.Parameters.AddWithValue("$md", memoryDigest); memoryCommand.Parameters.AddWithValue("$at", at.ToString("O")); memoryCommand.ExecuteNonQuery();
        }
        using (var projection = c.CreateCommand())
        {
            projection.Transaction = tx;
            projection.CommandText = "INSERT INTO memory_projection_entries(workspace_id,project_id,chapter_id,projection,entity_id,payload_json,digest,source_delta_id,revision,updated_at_utc) VALUES($w,$p,$c,'KNOWLEDGE',$e,'{}',$d,$s,1,$at);";
            projection.Parameters.AddWithValue("$w", workspace); projection.Parameters.AddWithValue("$p", project.ToString("D")); projection.Parameters.AddWithValue("$c", chapter); projection.Parameters.AddWithValue("$e", entity); projection.Parameters.AddWithValue("$d", projectionDigest); projection.Parameters.AddWithValue("$s", memory.ToString("D")); projection.Parameters.AddWithValue("$at", at.ToString("O")); projection.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

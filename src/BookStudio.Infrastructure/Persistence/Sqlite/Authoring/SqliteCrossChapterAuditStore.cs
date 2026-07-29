using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteCrossChapterAuditStore : ICrossChapterAuditStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteCrossChapterAuditStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<CrossChapterAuditCreateResult> CreateAsync(CrossChapterAuditDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(d);
        var hash = DraftHash(d);
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, d.WorkspaceId, d.AuditId);
            if (existing is not null)
            {
                var receipt = ReadReceipt(c, tx, d.AuditId) ?? throw new CrossChapterAuditConflictException("Create receipt missing.");
                RequireReceipt(receipt, "CREATE", d.WorkspaceId, d.AuditId, d.RequestFingerprint, hash);
                if (existing.PayloadHash != hash) throw new CrossChapterAuditConflictException("Audit identity reused with different payload.");
                return new CrossChapterAuditCreateResult(existing, true);
            }

            ValidateSnapshot(c, tx, d.WorkspaceId, d.ProjectId, d.Chapters);
            Execute(c, tx, "INSERT INTO cross_chapter_audits(workspace_id,audit_id,project_id,rule_set,chapters_json,findings_json,actor,evidence,payload_hash,revision,status,decision,decision_reason,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$r,$c,'[]',$a,$e,$h,1,'PROPOSED',NULL,NULL,NULL,$at,$at);",
                ("$w", d.WorkspaceId), ("$id", d.AuditId.ToString("D")), ("$p", d.ProjectId.ToString("D")), ("$r", d.RuleSet), ("$c", JsonSerializer.Serialize(d.Chapters)), ("$a", d.Actor), ("$e", d.Evidence), ("$h", hash), ("$at", Text(at)));
            InsertHistory(c, tx, d.WorkspaceId, d.AuditId, 1, CrossChapterAuditStatus.Proposed, d.Chapters, [], null, null, d.Actor, at);
            InsertReceipt(c, tx, d.AuditId, d.WorkspaceId, d.AuditId, "CREATE", d.RequestFingerprint, hash, 1, null, at);
            return new CrossChapterAuditCreateResult(Require(c, tx, d.WorkspaceId, d.AuditId), false);
        }, ct);
    }

    public ValueTask<CrossChapterAudit> EvaluateAsync(CrossChapterAuditControlCommand cmd, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.AuditId, "EVALUATE", cmd.RequestFingerprint, ControlHash(cmd), cmd.ExpectedRevision, at, (c, tx, audit) =>
        {
            if (audit.Status is not (CrossChapterAuditStatus.Proposed or CrossChapterAuditStatus.Reopened)) throw new CrossChapterAuditTransitionException("Only proposed or reopened audits can evaluate.");
            if (!SnapshotMatches(c, tx, audit)) return Terminal(c, tx, audit, CrossChapterAuditStatus.Stale, "editorial.cross-chapter-audit.stale", cmd.RequestId, cmd.Actor, at);
            var findings = EvaluateContinuity(c, tx, audit);
            var revision = audit.Revision + 1;
            Execute(c, tx, "UPDATE cross_chapter_audits SET findings_json=$f,status='EVALUATED',revision=$r,decision=NULL,decision_reason=NULL,message_id=NULL,updated_at_utc=$at WHERE workspace_id=$w AND audit_id=$id;",
                ("$f", JsonSerializer.Serialize(findings)), ("$r", revision), ("$at", Text(at)), ("$w", audit.WorkspaceId), ("$id", audit.AuditId.ToString("D")));
            InsertHistory(c, tx, audit.WorkspaceId, audit.AuditId, revision, CrossChapterAuditStatus.Evaluated, audit.Chapters, findings, null, null, cmd.Actor, at);
            return Require(c, tx, audit.WorkspaceId, audit.AuditId);
        }, ct);

    public ValueTask<CrossChapterAudit> DecideAsync(CrossChapterAuditDecisionCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason) || string.IsNullOrWhiteSpace(cmd.Actor)) throw new CrossChapterAuditValidationException("Decision reason and actor are required.");
        var hash = Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.AuditId, cmd.ExpectedRevision, cmd.Decision, cmd.Reason, cmd.Actor }));
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.AuditId, "DECIDE", cmd.RequestFingerprint, hash, cmd.ExpectedRevision, at, (c, tx, audit) =>
        {
            if (audit.Status != CrossChapterAuditStatus.Evaluated) throw new CrossChapterAuditTransitionException("Only evaluated audits can be decided.");
            if (!SnapshotMatches(c, tx, audit)) return Terminal(c, tx, audit, CrossChapterAuditStatus.Stale, "editorial.cross-chapter-audit.stale", cmd.RequestId, cmd.Actor, at);
            if (cmd.Decision == CrossChapterAuditDecision.Approve && audit.Findings.Any(x => x.Open && x.Severity == CrossChapterAuditSeverity.Blocking)) throw new CrossChapterAuditValidationException("Blocking findings prevent approval.");
            var status = cmd.Decision switch { CrossChapterAuditDecision.Approve => CrossChapterAuditStatus.Approved, CrossChapterAuditDecision.Reject => CrossChapterAuditStatus.Rejected, _ => CrossChapterAuditStatus.RepairRequired };
            var message = MessageId(cmd.RequestId);
            var revision = audit.Revision + 1;
            Execute(c, tx, "UPDATE cross_chapter_audits SET status=$s,decision=$d,decision_reason=$reason,message_id=$m,revision=$r,updated_at_utc=$at WHERE workspace_id=$w AND audit_id=$id;",
                ("$s", DbStatus(status)), ("$d", cmd.Decision.ToString().ToUpperInvariant()), ("$reason", cmd.Reason), ("$m", message.ToString("D")), ("$r", revision), ("$at", Text(at)), ("$w", audit.WorkspaceId), ("$id", audit.AuditId.ToString("D")));
            InsertHistory(c, tx, audit.WorkspaceId, audit.AuditId, revision, status, audit.Chapters, audit.Findings, cmd.Decision, cmd.Reason, cmd.Actor, at);
            InsertOutbox(c, tx, message, status == CrossChapterAuditStatus.Approved ? "editorial.cross-chapter-audit.approved" : status == CrossChapterAuditStatus.Rejected ? "editorial.cross-chapter-audit.rejected" : "editorial.cross-chapter-audit.repair-required", new { audit.WorkspaceId, audit.AuditId, audit.ProjectId, cmd.Decision, cmd.Reason, cmd.Actor }, at);
            return Require(c, tx, audit.WorkspaceId, audit.AuditId);
        }, ct);
    }

    public ValueTask<CrossChapterAudit> ReopenAsync(CrossChapterAuditReopenCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason) || string.IsNullOrWhiteSpace(cmd.Actor)) throw new CrossChapterAuditValidationException("Reopen reason and actor are required.");
        var hash = Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.AuditId, cmd.ExpectedRevision, cmd.Reason, cmd.Actor }));
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.AuditId, "REOPEN", cmd.RequestFingerprint, hash, cmd.ExpectedRevision, at, (c, tx, audit) =>
        {
            if (audit.Status is not (CrossChapterAuditStatus.Approved or CrossChapterAuditStatus.Rejected or CrossChapterAuditStatus.RepairRequired)) throw new CrossChapterAuditTransitionException("Only decided audits can reopen.");
            var message = MessageId(cmd.RequestId);
            var revision = audit.Revision + 1;
            Execute(c, tx, "UPDATE cross_chapter_audits SET status='REOPENED',decision=NULL,decision_reason=$reason,message_id=$m,revision=$r,updated_at_utc=$at WHERE workspace_id=$w AND audit_id=$id;",
                ("$reason", cmd.Reason), ("$m", message.ToString("D")), ("$r", revision), ("$at", Text(at)), ("$w", audit.WorkspaceId), ("$id", audit.AuditId.ToString("D")));
            InsertHistory(c, tx, audit.WorkspaceId, audit.AuditId, revision, CrossChapterAuditStatus.Reopened, audit.Chapters, audit.Findings, null, cmd.Reason, cmd.Actor, at);
            InsertOutbox(c, tx, message, "editorial.cross-chapter-audit.reopened", new { audit.WorkspaceId, audit.AuditId, audit.ProjectId, cmd.Reason, cmd.Actor }, at);
            return Require(c, tx, audit.WorkspaceId, audit.AuditId);
        }, ct);
    }

    public async ValueTask<CrossChapterAudit?> GetAsync(string workspaceId, Guid auditId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var c = _factory.OpenConnection();
        return await Task.FromResult(Read(c, null, workspaceId, auditId));
    }

    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false); }

    private ValueTask<CrossChapterAudit> Mutate(Guid requestId, string workspace, Guid auditId, string operation, string fingerprint, string hash, long expectedRevision, DateTimeOffset at, Func<SqliteConnection, SqliteTransaction, CrossChapterAudit, CrossChapterAudit> action, CancellationToken ct) =>
        _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var receipt = ReadReceipt(c, tx, requestId);
            if (receipt is not null) { RequireReceipt(receipt, operation, workspace, auditId, fingerprint, hash); return Require(c, tx, workspace, auditId); }
            var audit = Require(c, tx, workspace, auditId);
            if (audit.Revision != expectedRevision) throw new CrossChapterAuditConflictException("Stale revision.");
            var result = action(c, tx, audit);
            InsertReceipt(c, tx, requestId, workspace, auditId, operation, fingerprint, hash, result.Revision, result.MessageId, at);
            return result;
        }, ct);

    private static CrossChapterAudit Terminal(SqliteConnection c, SqliteTransaction tx, CrossChapterAudit audit, CrossChapterAuditStatus status, string eventType, Guid requestId, string actor, DateTimeOffset at)
    {
        var message = MessageId(requestId); var revision = audit.Revision + 1;
        Execute(c, tx, "UPDATE cross_chapter_audits SET status=$s,message_id=$m,revision=$r,updated_at_utc=$at WHERE workspace_id=$w AND audit_id=$id;", ("$s", DbStatus(status)), ("$m", message.ToString("D")), ("$r", revision), ("$at", Text(at)), ("$w", audit.WorkspaceId), ("$id", audit.AuditId.ToString("D")));
        InsertHistory(c, tx, audit.WorkspaceId, audit.AuditId, revision, status, audit.Chapters, audit.Findings, audit.Decision, audit.DecisionReason, actor, at);
        InsertOutbox(c, tx, message, eventType, new { audit.WorkspaceId, audit.AuditId, audit.ProjectId, Actor = actor }, at);
        return Require(c, tx, audit.WorkspaceId, audit.AuditId);
    }

    private static void ValidateDraft(CrossChapterAuditDraft d)
    {
        if (d.AuditId == Guid.Empty || d.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(d.WorkspaceId) || string.IsNullOrWhiteSpace(d.RuleSet) || d.Chapters.Count < 2 || string.IsNullOrWhiteSpace(d.Actor) || string.IsNullOrWhiteSpace(d.Evidence) || string.IsNullOrWhiteSpace(d.RequestFingerprint)) throw new CrossChapterAuditValidationException("Complete cross chapter audit is required.");
        if (d.Chapters.Any(x => string.IsNullOrWhiteSpace(x.ChapterId) || x.GateId == Guid.Empty || x.MemoryCommitId == Guid.Empty || x.LockedVersion < 1 || string.IsNullOrWhiteSpace(x.LockedDigest) || string.IsNullOrWhiteSpace(x.MemoryDigest))) throw new CrossChapterAuditValidationException("Snapshot items must be complete.");
        if (d.Chapters.Select(x => x.ChapterId).Distinct(StringComparer.Ordinal).Count() != d.Chapters.Count) throw new CrossChapterAuditValidationException("Duplicate chapters are forbidden.");
    }

    private static void ValidateSnapshot(SqliteConnection c, SqliteTransaction tx, string workspace, Guid project, IReadOnlyList<CrossChapterSnapshotItem> chapters)
    {
        foreach (var item in chapters)
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT 1 FROM chapter_gate_locks l JOIN memory_deltas d ON d.workspace_id=l.workspace_id AND d.project_id=l.project_id AND d.chapter_id=l.chapter_id AND d.gate_id=l.gate_id WHERE l.workspace_id=$w AND l.project_id=$p AND l.chapter_id=$c AND l.gate_id=$g AND l.locked_version=$v AND l.locked_digest=$ld AND l.reopened_at_utc IS NULL AND d.delta_id=$m AND d.status='COMMITTED' AND d.payload_hash=$md;";
            cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$p", project.ToString("D")); cmd.Parameters.AddWithValue("$c", item.ChapterId); cmd.Parameters.AddWithValue("$g", item.GateId.ToString("D")); cmd.Parameters.AddWithValue("$v", item.LockedVersion); cmd.Parameters.AddWithValue("$ld", item.LockedDigest); cmd.Parameters.AddWithValue("$m", item.MemoryCommitId.ToString("D")); cmd.Parameters.AddWithValue("$md", item.MemoryDigest);
            if (cmd.ExecuteScalar() is null) throw new CrossChapterAuditValidationException("Exact active lock and committed memory snapshot were not found.");
        }
    }

    private static bool SnapshotMatches(SqliteConnection c, SqliteTransaction tx, CrossChapterAudit audit) { try { ValidateSnapshot(c, tx, audit.WorkspaceId, audit.ProjectId, audit.Chapters); return true; } catch (CrossChapterAuditValidationException) { return false; } }

    private static List<CrossChapterAuditFinding> EvaluateContinuity(SqliteConnection c, SqliteTransaction tx, CrossChapterAudit audit)
    {
        var findings = new List<CrossChapterAuditFinding>();
        for (var i = 1; i < audit.Chapters.Count; i++)
        {
            var previous = audit.Chapters[i - 1]; var current = audit.Chapters[i];
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT projection,entity_id,COUNT(DISTINCT digest) FROM memory_projection_entries WHERE workspace_id=$w AND project_id=$p AND chapter_id IN ($a,$b) GROUP BY projection,entity_id HAVING COUNT(DISTINCT digest)>1;";
            cmd.Parameters.AddWithValue("$w", audit.WorkspaceId); cmd.Parameters.AddWithValue("$p", audit.ProjectId.ToString("D")); cmd.Parameters.AddWithValue("$a", previous.ChapterId); cmd.Parameters.AddWithValue("$b", current.ChapterId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) findings.Add(new(Guid.NewGuid(), "PROJECTION_DRIFT", CrossChapterAuditSeverity.Warning, [previous.ChapterId, current.ChapterId], $"{reader.GetString(0)}:{reader.GetString(1)}", "Distinct committed projection digests across adjacent chapters.", true));
        }
        return findings;
    }

    private sealed record Receipt(string Workspace, Guid AuditId, string Operation, string Fingerprint, string PayloadHash);
    private static Receipt? ReadReceipt(SqliteConnection c, SqliteTransaction tx, Guid id) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT workspace_id,audit_id,operation,request_fingerprint,payload_hash FROM cross_chapter_audit_requests WHERE request_id=$id;"; cmd.Parameters.AddWithValue("$id", id.ToString("D")); using var r = cmd.ExecuteReader(); return r.Read() ? new(r.GetString(0), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4)) : null; }
    private static void RequireReceipt(Receipt r, string op, string workspace, Guid auditId, string fp, string hash) { if (r.Operation != op || r.Workspace != workspace || r.AuditId != auditId || r.Fingerprint != fp || r.PayloadHash != hash) throw new CrossChapterAuditConflictException("Request id reused with different payload."); }
    private static void InsertReceipt(SqliteConnection c, SqliteTransaction tx, Guid request, string workspace, Guid auditId, string op, string fp, string hash, long revision, Guid? message, DateTimeOffset at) => Execute(c, tx, "INSERT INTO cross_chapter_audit_requests(request_id,workspace_id,audit_id,operation,request_fingerprint,payload_hash,result_revision,message_id,created_at_utc) VALUES($q,$w,$a,$o,$f,$h,$r,$m,$at);", ("$q", request.ToString("D")), ("$w", workspace), ("$a", auditId.ToString("D")), ("$o", op), ("$f", fp), ("$h", hash), ("$r", revision), ("$m", message is null ? DBNull.Value : message.Value.ToString("D")), ("$at", Text(at)));
    private static void InsertHistory(SqliteConnection c, SqliteTransaction tx, string workspace, Guid id, long revision, CrossChapterAuditStatus status, IReadOnlyList<CrossChapterSnapshotItem> chapters, IReadOnlyList<CrossChapterAuditFinding> findings, CrossChapterAuditDecision? decision, string? reason, string actor, DateTimeOffset at) => Execute(c, tx, "INSERT INTO cross_chapter_audit_history(history_id,workspace_id,audit_id,revision,status,chapters_json,findings_json,decision,decision_reason,actor,occurred_at_utc) VALUES($h,$w,$a,$r,$s,$c,$f,$d,$dr,$actor,$at);", ("$h", Guid.NewGuid().ToString("D")), ("$w", workspace), ("$a", id.ToString("D")), ("$r", revision), ("$s", DbStatus(status)), ("$c", JsonSerializer.Serialize(chapters)), ("$f", JsonSerializer.Serialize(findings)), ("$d", decision is null ? DBNull.Value : decision.Value.ToString().ToUpperInvariant()), ("$dr", reason is null ? DBNull.Value : reason), ("$actor", actor), ("$at", Text(at)));
    private static CrossChapterAudit? Read(SqliteConnection c, SqliteTransaction? tx, string workspace, Guid id) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT project_id,rule_set,chapters_json,findings_json,actor,evidence,payload_hash,revision,status,decision,decision_reason,message_id,created_at_utc,updated_at_utc FROM cross_chapter_audits WHERE workspace_id=$w AND audit_id=$id;"; cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$id", id.ToString("D")); using var r = cmd.ExecuteReader(); if (!r.Read()) return null; return new(id, Guid.Parse(r.GetString(0)), workspace, r.GetString(1), JsonSerializer.Deserialize<List<CrossChapterSnapshotItem>>(r.GetString(2)) ?? [], JsonSerializer.Deserialize<List<CrossChapterAuditFinding>>(r.GetString(3)) ?? [], r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt64(7), ParseStatus(r.GetString(8)), r.IsDBNull(9) ? null : Enum.Parse<CrossChapterAuditDecision>(r.GetString(9), true), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : Guid.Parse(r.GetString(11)), Parse(r.GetString(12)), Parse(r.GetString(13))); }
    private static CrossChapterAudit Require(SqliteConnection c, SqliteTransaction tx, string workspace, Guid id) => Read(c, tx, workspace, id) ?? throw new KeyNotFoundException("Cross chapter audit not found.");
    private static CrossChapterAuditStatus ParseStatus(string value) => value switch { "REPAIR_REQUIRED" => CrossChapterAuditStatus.RepairRequired, _ => Enum.Parse<CrossChapterAuditStatus>(value, true) };
    private static string DbStatus(CrossChapterAuditStatus value) => value == CrossChapterAuditStatus.RepairRequired ? "REPAIR_REQUIRED" : value.ToString().ToUpperInvariant();
    private static string DraftHash(CrossChapterAuditDraft d) => Hash(JsonSerializer.Serialize(new { d.ProjectId, d.WorkspaceId, d.RuleSet, d.Chapters, d.Actor, d.Evidence }));
    private static string ControlHash(CrossChapterAuditControlCommand c) => Hash(JsonSerializer.Serialize(new { c.WorkspaceId, c.AuditId, c.ExpectedRevision, c.Actor }));
    private static void InsertOutbox(SqliteConnection c, SqliteTransaction tx, Guid message, string type, object payload, DateTimeOffset at) => Execute(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($m,$t,'1.0.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);", ("$m", message.ToString("D")), ("$t", type), ("$p", JsonSerializer.Serialize(payload)), ("$at", Text(at)));
    private static Guid MessageId(Guid request) => new(request.ToByteArray().Select((b, i) => (byte)(b ^ (i % 2 == 0 ? 0x33 : 0xCC))).ToArray());
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Text(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Name, p.Value); cmd.ExecuteNonQuery(); }
}
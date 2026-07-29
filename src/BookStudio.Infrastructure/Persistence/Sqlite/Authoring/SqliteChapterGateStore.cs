using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteChapterGateStore : IChapterGateStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteChapterGateStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<ChapterGateCreateResult> CreateAsync(ChapterGateDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(d);
        var hash = Hash(JsonSerializer.Serialize(new { d.ProjectId, d.WorkspaceId, d.ChapterId, d.ExpectedVersion, d.ExpectedDigest, d.Actor }));
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, d.WorkspaceId, d.GateId);
            if (existing is not null)
            {
                var receipt = ReadReceipt(c, tx, d.GateId) ?? throw new ChapterGateConflictException("Create receipt missing.");
                RequireReceipt(receipt, "CREATE", d.WorkspaceId, d.GateId, d.RequestFingerprint, hash);
                return new ChapterGateCreateResult(existing, true);
            }
            var target = ReadTarget(c, tx, d.WorkspaceId, d.ChapterId) ?? throw new ChapterGateValidationException("Chapter target not found.");
            if (target.Version != d.ExpectedVersion || target.Digest != d.ExpectedDigest) throw new ChapterGateConflictException("Chapter version or digest changed.");
            Execute(c, tx, "INSERT INTO chapter_gates(workspace_id,gate_id,project_id,chapter_id,expected_version,expected_digest,findings_json,actor,revision,status,decision,decision_reason,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$c,$v,$d,'[]',$a,1,'PROPOSED',NULL,NULL,NULL,$at,$at);",
                ("$w", d.WorkspaceId), ("$id", d.GateId.ToString("D")), ("$p", d.ProjectId.ToString("D")), ("$c", d.ChapterId), ("$v", d.ExpectedVersion), ("$d", d.ExpectedDigest), ("$a", d.Actor), ("$at", Text(at)));
            InsertReceipt(c, tx, d.GateId, d.WorkspaceId, d.GateId, "CREATE", d.RequestFingerprint, hash, 1, null, at);
            return new ChapterGateCreateResult(Require(c, tx, d.WorkspaceId, d.GateId), false);
        }, ct);
    }

    public ValueTask<ChapterGate> EvaluateAsync(ChapterGateControlCommand cmd, DateTimeOffset at, CancellationToken ct = default) => Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.GateId, "EVALUATE", cmd.RequestFingerprint, ControlHash(cmd), cmd.ExpectedRevision, at, (c, tx, gate) =>
    {
        if (gate.Status is not (ChapterGateStatus.Proposed or ChapterGateStatus.Reopened)) throw new ChapterGateTransitionException("Only proposed or reopened gates can evaluate.");
        var target = ReadTarget(c, tx, gate.WorkspaceId, gate.ChapterId) ?? throw new ChapterGateValidationException("Chapter target not found.");
        if (target.Version != gate.ExpectedVersion || target.Digest != gate.ExpectedDigest) throw new ChapterGateConflictException("Chapter changed before evaluation.");
        var findings = ReadFindings(c, tx, gate.WorkspaceId, gate.ProjectId);
        Execute(c, tx, "UPDATE chapter_gates SET findings_json=$f,status='EVALUATED',revision=revision+1,updated_at_utc=$at WHERE workspace_id=$w AND gate_id=$id;", ("$f", JsonSerializer.Serialize(findings)), ("$at", Text(at)), ("$w", gate.WorkspaceId), ("$id", gate.GateId.ToString("D")));
        return Require(c, tx, gate.WorkspaceId, gate.GateId);
    }, ct);

    public ValueTask<ChapterGate> DecideAsync(ChapterGateDecisionCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason) || string.IsNullOrWhiteSpace(cmd.Actor)) throw new ChapterGateValidationException("Decision reason and actor are required.");
        var hash = Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.GateId, cmd.ExpectedRevision, cmd.Decision, cmd.Reason, cmd.Actor }));
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.GateId, "DECIDE", cmd.RequestFingerprint, hash, cmd.ExpectedRevision, at, (c, tx, gate) =>
        {
            if (gate.Status != ChapterGateStatus.Evaluated) throw new ChapterGateTransitionException("Only evaluated gates can be decided.");
            var target = ReadTarget(c, tx, gate.WorkspaceId, gate.ChapterId) ?? throw new ChapterGateValidationException("Chapter target not found.");
            if (target.Version != gate.ExpectedVersion || target.Digest != gate.ExpectedDigest) throw new ChapterGateConflictException("Chapter changed before decision.");
            if (cmd.Decision == ChapterGateDecision.Approve && gate.Findings.Any(x => x.Blocking)) throw new ChapterGateValidationException("Blocking findings prevent approval.");
            var status = cmd.Decision switch { ChapterGateDecision.Approve => ChapterGateStatus.Locked, ChapterGateDecision.Reject => ChapterGateStatus.Rejected, _ => ChapterGateStatus.RepairRequired };
            var message = MessageId(cmd.RequestId);
            Execute(c, tx, "UPDATE chapter_gates SET status=$s,decision=$d,decision_reason=$r,message_id=$m,revision=revision+1,updated_at_utc=$at WHERE workspace_id=$w AND gate_id=$id;",
                ("$s", DbStatus(status)), ("$d", cmd.Decision.ToString().ToUpperInvariant()), ("$r", cmd.Reason), ("$m", message.ToString("D")), ("$at", Text(at)), ("$w", gate.WorkspaceId), ("$id", gate.GateId.ToString("D")));
            if (status == ChapterGateStatus.Locked)
                Execute(c, tx, "INSERT INTO chapter_gate_locks(workspace_id,chapter_id,gate_id,project_id,locked_version,locked_digest,locked_at_utc,reopened_at_utc) VALUES($w,$c,$g,$p,$v,$d,$at,NULL) ON CONFLICT(workspace_id,chapter_id) DO UPDATE SET gate_id=excluded.gate_id,project_id=excluded.project_id,locked_version=excluded.locked_version,locked_digest=excluded.locked_digest,locked_at_utc=excluded.locked_at_utc,reopened_at_utc=NULL;", ("$w", gate.WorkspaceId), ("$c", gate.ChapterId), ("$g", gate.GateId.ToString("D")), ("$p", gate.ProjectId.ToString("D")), ("$v", gate.ExpectedVersion), ("$d", gate.ExpectedDigest), ("$at", Text(at)));
            InsertOutbox(c, tx, message, status == ChapterGateStatus.Locked ? "editorial.chapter-gate.locked" : status == ChapterGateStatus.Rejected ? "editorial.chapter-gate.rejected" : "editorial.chapter-gate.repair-required", new { gate.WorkspaceId, gate.GateId, gate.ProjectId, gate.ChapterId, cmd.Decision, cmd.Reason, cmd.Actor }, at);
            return Require(c, tx, gate.WorkspaceId, gate.GateId);
        }, ct);
    }

    public ValueTask<ChapterGate> ReopenAsync(ChapterGateReopenCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason) || string.IsNullOrWhiteSpace(cmd.Actor)) throw new ChapterGateValidationException("Reopen reason and actor are required.");
        var hash = Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.GateId, cmd.ExpectedRevision, cmd.Reason, cmd.Actor }));
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.GateId, "REOPEN", cmd.RequestFingerprint, hash, cmd.ExpectedRevision, at, (c, tx, gate) =>
        {
            if (gate.Status != ChapterGateStatus.Locked) throw new ChapterGateTransitionException("Only locked gates can reopen.");
            var message = MessageId(cmd.RequestId);
            Execute(c, tx, "UPDATE chapter_gates SET status='REOPENED',decision_reason=$r,message_id=$m,revision=revision+1,updated_at_utc=$at WHERE workspace_id=$w AND gate_id=$id;", ("$r", cmd.Reason), ("$m", message.ToString("D")), ("$at", Text(at)), ("$w", gate.WorkspaceId), ("$id", gate.GateId.ToString("D")));
            Execute(c, tx, "UPDATE chapter_gate_locks SET reopened_at_utc=$at WHERE workspace_id=$w AND chapter_id=$c AND gate_id=$g;", ("$at", Text(at)), ("$w", gate.WorkspaceId), ("$c", gate.ChapterId), ("$g", gate.GateId.ToString("D")));
            InsertOutbox(c, tx, message, "editorial.chapter-gate.reopened", new { gate.WorkspaceId, gate.GateId, gate.ProjectId, gate.ChapterId, cmd.Reason, cmd.Actor }, at);
            return Require(c, tx, gate.WorkspaceId, gate.GateId);
        }, ct);
    }

    public async ValueTask<ChapterGate?> GetAsync(string workspaceId, Guid gateId, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); using var c = _factory.OpenConnection(); return await Task.FromResult(Read(c, null, workspaceId, gateId)); }
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false); }

    private ValueTask<ChapterGate> Mutate(Guid requestId, string workspace, Guid gateId, string operation, string fingerprint, string hash, long expectedRevision, DateTimeOffset at, Func<SqliteConnection, SqliteTransaction, ChapterGate, ChapterGate> action, CancellationToken ct) => _queue.ExecuteInTransactionAsync((c, tx, token) =>
    {
        token.ThrowIfCancellationRequested();
        var receipt = ReadReceipt(c, tx, requestId);
        if (receipt is not null) { RequireReceipt(receipt, operation, workspace, gateId, fingerprint, hash); return Require(c, tx, workspace, gateId); }
        var gate = Require(c, tx, workspace, gateId);
        if (gate.Revision != expectedRevision) throw new ChapterGateConflictException("Stale revision.");
        var result = action(c, tx, gate);
        InsertReceipt(c, tx, requestId, workspace, gateId, operation, fingerprint, hash, result.Revision, result.MessageId, at);
        return result;
    }, ct);

    private static IReadOnlyList<ChapterGateFinding> ReadFindings(SqliteConnection c, SqliteTransaction tx, string workspace, Guid project)
    {
        var result = new List<ChapterGateFinding>();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT json_extract(f.value,'$.FindingId'),json_extract(f.value,'$.Code'),json_extract(f.value,'$.Message'),json_extract(f.value,'$.Severity') FROM transition_audits a, json_each(a.findings_json) f WHERE a.workspace_id=$w AND a.project_id=$p AND a.status='CLOSED';";
        cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$p", project.ToString("D"));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = Guid.TryParse(r.IsDBNull(0) ? null : r.GetString(0), out var parsed) ? parsed : Guid.NewGuid();
            var severity = r.IsDBNull(3) ? string.Empty : r.GetValue(3).ToString() ?? string.Empty;
            result.Add(new(id, r.IsDBNull(1) ? "AUDIT_FINDING" : r.GetString(1), r.IsDBNull(2) ? "Audit finding" : r.GetString(2), severity.Equals("BLOCKING", StringComparison.OrdinalIgnoreCase) || severity == "2", "transition-audit"));
        }
        return result;
    }

    private sealed record Target(int Version, string Digest);
    private static Target? ReadTarget(SqliteConnection c, SqliteTransaction tx, string workspace, string chapterId) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT version,digest FROM repair_patch_targets WHERE workspace_id=$w AND artifact_id=$a;"; cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$a", chapterId); using var r = cmd.ExecuteReader(); return r.Read() ? new(r.GetInt32(0), r.GetString(1)) : null; }
    private static void ValidateDraft(ChapterGateDraft d) { if (d.GateId == Guid.Empty || d.ProjectId == Guid.Empty || d.ExpectedVersion < 1 || string.IsNullOrWhiteSpace(d.WorkspaceId) || string.IsNullOrWhiteSpace(d.ChapterId) || string.IsNullOrWhiteSpace(d.ExpectedDigest) || string.IsNullOrWhiteSpace(d.Actor) || string.IsNullOrWhiteSpace(d.RequestFingerprint)) throw new ChapterGateValidationException("Complete chapter gate is required."); }
    private static string ControlHash(ChapterGateControlCommand c) => Hash(JsonSerializer.Serialize(new { c.WorkspaceId, c.GateId, c.ExpectedRevision, c.Actor }));
    private sealed record Receipt(string Workspace, Guid GateId, string Operation, string Fingerprint, string PayloadHash);
    private static Receipt? ReadReceipt(SqliteConnection c, SqliteTransaction tx, Guid id) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT workspace_id,gate_id,operation,request_fingerprint,payload_hash FROM chapter_gate_requests WHERE request_id=$id;"; cmd.Parameters.AddWithValue("$id", id.ToString("D")); using var r = cmd.ExecuteReader(); return r.Read() ? new(r.GetString(0), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4)) : null; }
    private static void RequireReceipt(Receipt r, string op, string workspace, Guid gateId, string fp, string hash) { if (r.Operation != op || r.Workspace != workspace || r.GateId != gateId || r.Fingerprint != fp || r.PayloadHash != hash) throw new ChapterGateConflictException("Request id reused with different payload."); }
    private static void InsertReceipt(SqliteConnection c, SqliteTransaction tx, Guid request, string workspace, Guid gateId, string op, string fp, string hash, long revision, Guid? message, DateTimeOffset at) => Execute(c, tx, "INSERT INTO chapter_gate_requests(request_id,workspace_id,gate_id,operation,request_fingerprint,payload_hash,result_revision,message_id,created_at_utc) VALUES($q,$w,$g,$o,$f,$h,$r,$m,$at);", ("$q", request.ToString("D")), ("$w", workspace), ("$g", gateId.ToString("D")), ("$o", op), ("$f", fp), ("$h", hash), ("$r", revision), ("$m", message is null ? DBNull.Value : message.Value.ToString("D")), ("$at", Text(at)));
    private static ChapterGate? Read(SqliteConnection c, SqliteTransaction? tx, string workspace, Guid id) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT project_id,chapter_id,expected_version,expected_digest,findings_json,actor,revision,status,decision,decision_reason,message_id,created_at_utc,updated_at_utc FROM chapter_gates WHERE workspace_id=$w AND gate_id=$id;"; cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$id", id.ToString("D")); using var r = cmd.ExecuteReader(); if (!r.Read()) return null; return new(id, Guid.Parse(r.GetString(0)), workspace, r.GetString(1), r.GetInt32(2), r.GetString(3), JsonSerializer.Deserialize<List<ChapterGateFinding>>(r.GetString(4)) ?? [], r.GetString(5), r.GetInt64(6), ParseStatus(r.GetString(7)), r.IsDBNull(8) ? null : Enum.Parse<ChapterGateDecision>(r.GetString(8), true), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : Guid.Parse(r.GetString(10)), Parse(r.GetString(11)), Parse(r.GetString(12))); }
    private static ChapterGate Require(SqliteConnection c, SqliteTransaction tx, string workspace, Guid id) => Read(c, tx, workspace, id) ?? throw new KeyNotFoundException("Chapter gate not found.");
    private static ChapterGateStatus ParseStatus(string value) => value == "REPAIRREQUIRED" ? ChapterGateStatus.RepairRequired : Enum.Parse<ChapterGateStatus>(value, true);
    private static string DbStatus(ChapterGateStatus value) => value == ChapterGateStatus.RepairRequired ? "REPAIRREQUIRED" : value.ToString().ToUpperInvariant();
    private static void InsertOutbox(SqliteConnection c, SqliteTransaction tx, Guid message, string type, object payload, DateTimeOffset at) => Execute(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($m,$t,'1.0.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);", ("$m", message.ToString("D")), ("$t", type), ("$p", JsonSerializer.Serialize(payload)), ("$at", Text(at)));
    private static Guid MessageId(Guid request) => new(request.ToByteArray().Select((b, i) => (byte)(b ^ (i % 2 == 0 ? 0x33 : 0xCC))).ToArray());
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Text(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Name, p.Value); cmd.ExecuteNonQuery(); }
}
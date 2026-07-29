using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteMemoryCommitStore : IMemoryCommitStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteMemoryCommitStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<MemoryDeltaCreateResult> ProposeAsync(MemoryDeltaDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(d);
        var hash = DraftHash(d);
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, d.WorkspaceId, d.DeltaId);
            if (existing is not null)
            {
                var receipt = ReadReceipt(c, tx, d.DeltaId) ?? throw new MemoryDeltaConflictException("Proposal receipt missing.");
                RequireReceipt(receipt, "PROPOSE", d.WorkspaceId, d.DeltaId, d.RequestFingerprint, hash);
                if (existing.PayloadHash != hash) throw new MemoryDeltaConflictException("Delta identity reused with different payload.");
                return new MemoryDeltaCreateResult(existing, true);
            }
            RequireCurrentLock(c, tx, d.WorkspaceId, d.ProjectId, d.ChapterId, d.GateId, d.LockedVersion, d.LockedDigest);
            Execute(c, tx, "INSERT INTO memory_deltas(workspace_id,delta_id,project_id,chapter_id,gate_id,locked_version,locked_digest,entries_json,evidence,actor,payload_hash,revision,status,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$c,$g,$v,$d,$e,$ev,$a,$h,1,'PROPOSED',NULL,$at,$at);",
                ("$w", d.WorkspaceId), ("$id", d.DeltaId.ToString("D")), ("$p", d.ProjectId.ToString("D")), ("$c", d.ChapterId), ("$g", d.GateId.ToString("D")), ("$v", d.LockedVersion), ("$d", d.LockedDigest), ("$e", JsonSerializer.Serialize(d.Entries)), ("$ev", d.Evidence), ("$a", d.Actor), ("$h", hash), ("$at", Text(at)));
            InsertReceipt(c, tx, d.DeltaId, d.WorkspaceId, d.DeltaId, "PROPOSE", d.RequestFingerprint, hash, 1, null, at);
            return new MemoryDeltaCreateResult(Require(c, tx, d.WorkspaceId, d.DeltaId), false);
        }, ct);
    }

    public ValueTask<MemoryDelta> ValidateAsync(MemoryDeltaControlCommand cmd, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.DeltaId, "VALIDATE", cmd.RequestFingerprint, ControlHash(cmd), cmd.ExpectedRevision, at, (c, tx, d) =>
        {
            if (d.Status != MemoryDeltaStatus.Proposed) throw new MemoryDeltaTransitionException("Only proposed deltas can validate.");
            if (!LockMatches(c, tx, d)) return Terminal(c, tx, d, MemoryDeltaStatus.Stale, "editorial.memory-delta.stale", cmd.RequestId, cmd.Actor, at);
            foreach (var entry in d.Entries) ValidateEntry(c, tx, d, entry);
            UpdateStatus(c, tx, d, MemoryDeltaStatus.Validated, d.Revision + 1, null, at);
            return Require(c, tx, d.WorkspaceId, d.DeltaId);
        }, ct);

    public ValueTask<MemoryDelta> CommitAsync(MemoryDeltaControlCommand cmd, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.DeltaId, "COMMIT", cmd.RequestFingerprint, ControlHash(cmd), cmd.ExpectedRevision, at, (c, tx, d) =>
        {
            if (d.Status != MemoryDeltaStatus.Validated) throw new MemoryDeltaTransitionException("Only validated deltas can commit.");
            if (!LockMatches(c, tx, d)) return Terminal(c, tx, d, MemoryDeltaStatus.Stale, "editorial.memory-delta.stale", cmd.RequestId, cmd.Actor, at);
            var previous = new List<object>();
            foreach (var entry in d.Entries)
            {
                ValidateEntry(c, tx, d, entry);
                var old = ReadProjection(c, tx, d.WorkspaceId, entry.Projection, entry.EntityId);
                previous.Add(new { entry.Projection, entry.EntityId, Previous = old });
                ApplyEntry(c, tx, d, entry, old, at);
            }
            Execute(c, tx, "INSERT INTO memory_delta_history(history_id,workspace_id,delta_id,revision,status,snapshot_json,actor,occurred_at_utc) VALUES($h,$w,$d,$r,'COMMITTED',$s,$a,$at);",
                ("$h", Guid.NewGuid().ToString("D")), ("$w", d.WorkspaceId), ("$d", d.DeltaId.ToString("D")), ("$r", d.Revision + 1), ("$s", JsonSerializer.Serialize(previous)), ("$a", cmd.Actor), ("$at", Text(at)));
            var message = MessageId(cmd.RequestId);
            UpdateStatus(c, tx, d, MemoryDeltaStatus.Committed, d.Revision + 1, message, at);
            InsertOutbox(c, tx, message, "editorial.memory-delta.committed", new { d.WorkspaceId, d.DeltaId, d.ProjectId, d.ChapterId, d.GateId, EntryCount = d.Entries.Count, cmd.Actor }, at);
            return Require(c, tx, d.WorkspaceId, d.DeltaId);
        }, ct);

    public ValueTask<MemoryDelta> RejectAsync(MemoryDeltaDecisionCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason) || string.IsNullOrWhiteSpace(cmd.Actor)) throw new MemoryDeltaValidationException("Rejection reason and actor are required.");
        var hash = Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.DeltaId, cmd.ExpectedRevision, cmd.Reason, cmd.Actor }));
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.DeltaId, "REJECT", cmd.RequestFingerprint, hash, cmd.ExpectedRevision, at, (c, tx, d) =>
        {
            if (d.Status is MemoryDeltaStatus.Committed or MemoryDeltaStatus.Rejected or MemoryDeltaStatus.Stale) throw new MemoryDeltaTransitionException("Terminal delta cannot be rejected.");
            return Terminal(c, tx, d, MemoryDeltaStatus.Rejected, "editorial.memory-delta.rejected", cmd.RequestId, cmd.Actor, at, cmd.Reason);
        }, ct);
    }

    public async ValueTask<MemoryDelta?> GetAsync(string workspaceId, Guid deltaId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var c = _factory.OpenConnection();
        return await Task.FromResult(Read(c, null, workspaceId, deltaId));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false);
    }

    private ValueTask<MemoryDelta> Mutate(Guid requestId, string workspace, Guid deltaId, string operation, string fingerprint, string payloadHash, long expectedRevision, DateTimeOffset at, Func<SqliteConnection, SqliteTransaction, MemoryDelta, MemoryDelta> action, CancellationToken ct) =>
        _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var receipt = ReadReceipt(c, tx, requestId);
            if (receipt is not null)
            {
                RequireReceipt(receipt, operation, workspace, deltaId, fingerprint, payloadHash);
                return Require(c, tx, workspace, deltaId);
            }
            var delta = Require(c, tx, workspace, deltaId);
            if (delta.Revision != expectedRevision) throw new MemoryDeltaConflictException("Stale revision.");
            var result = action(c, tx, delta);
            InsertReceipt(c, tx, requestId, workspace, deltaId, operation, fingerprint, payloadHash, result.Revision, result.MessageId, at);
            return result;
        }, ct);

    private static MemoryDelta Terminal(SqliteConnection c, SqliteTransaction tx, MemoryDelta d, MemoryDeltaStatus status, string eventType, Guid requestId, string actor, DateTimeOffset at, string? reason = null)
    {
        var message = MessageId(requestId);
        UpdateStatus(c, tx, d, status, d.Revision + 1, message, at);
        InsertOutbox(c, tx, message, eventType, new { d.WorkspaceId, d.DeltaId, d.ProjectId, d.ChapterId, d.GateId, Actor = actor, Reason = reason }, at);
        return Require(c, tx, d.WorkspaceId, d.DeltaId);
    }

    private static void ValidateDraft(MemoryDeltaDraft d)
    {
        if (d.DeltaId == Guid.Empty || d.ProjectId == Guid.Empty || d.GateId == Guid.Empty || d.LockedVersion < 1 || string.IsNullOrWhiteSpace(d.WorkspaceId) || string.IsNullOrWhiteSpace(d.ChapterId) || string.IsNullOrWhiteSpace(d.LockedDigest) || d.Entries.Count == 0 || string.IsNullOrWhiteSpace(d.Evidence) || string.IsNullOrWhiteSpace(d.Actor) || string.IsNullOrWhiteSpace(d.RequestFingerprint)) throw new MemoryDeltaValidationException("Complete memory delta is required.");
        if (d.Entries.Any(e => string.IsNullOrWhiteSpace(e.Projection) || string.IsNullOrWhiteSpace(e.EntityId) || string.IsNullOrWhiteSpace(e.Operation) || string.IsNullOrWhiteSpace(e.PayloadJson))) throw new MemoryDeltaValidationException("Memory entries must be complete.");
        if (d.Entries.Select(e => (e.Projection, e.EntityId)).Distinct().Count() != d.Entries.Count) throw new MemoryDeltaValidationException("Duplicate projection entities are forbidden.");
        foreach (var e in d.Entries) { if (e.Operation.ToUpperInvariant() is not ("UPSERT" or "RETRACT")) throw new MemoryDeltaValidationException("Unsupported memory operation."); JsonDocument.Parse(e.PayloadJson).Dispose(); }
    }

    private static void RequireCurrentLock(SqliteConnection c, SqliteTransaction tx, string workspace, Guid project, string chapter, Guid gate, int version, string digest)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM chapter_gate_locks WHERE workspace_id=$w AND project_id=$p AND chapter_id=$c AND gate_id=$g AND locked_version=$v AND locked_digest=$d AND reopened_at_utc IS NULL;";
        cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$p", project.ToString("D")); cmd.Parameters.AddWithValue("$c", chapter); cmd.Parameters.AddWithValue("$g", gate.ToString("D")); cmd.Parameters.AddWithValue("$v", version); cmd.Parameters.AddWithValue("$d", digest);
        if (cmd.ExecuteScalar() is null) throw new MemoryDeltaValidationException("Exact active chapter lock was not found.");
    }

    private static bool LockMatches(SqliteConnection c, SqliteTransaction tx, MemoryDelta d)
    {
        try { RequireCurrentLock(c, tx, d.WorkspaceId, d.ProjectId, d.ChapterId, d.GateId, d.LockedVersion, d.LockedDigest); return true; }
        catch (MemoryDeltaValidationException) { return false; }
    }

    private sealed record Projection(string Payload, string Digest, long Revision);
    private static Projection? ReadProjection(SqliteConnection c, SqliteTransaction tx, string workspace, string projection, string entity)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT payload_json,digest,revision FROM memory_projection_entries WHERE workspace_id=$w AND projection=$p AND entity_id=$e;";
        cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$p", projection); cmd.Parameters.AddWithValue("$e", entity);
        using var r = cmd.ExecuteReader(); return r.Read() ? new(r.GetString(0), r.GetString(1), r.GetInt64(2)) : null;
    }

    private static void ValidateEntry(SqliteConnection c, SqliteTransaction tx, MemoryDelta d, MemoryDeltaEntry entry)
    {
        if (entry.Projection.ToUpperInvariant() is not ("KNOWLEDGE" or "STATE" or "TIMELINE" or "PLOT_THREAD")) throw new MemoryDeltaValidationException("Unsupported projection.");
        JsonDocument.Parse(entry.PayloadJson).Dispose();
        var old = ReadProjection(c, tx, d.WorkspaceId, entry.Projection, entry.EntityId);
        if (!string.IsNullOrWhiteSpace(entry.ExpectedDigest) && old?.Digest != entry.ExpectedDigest) throw new MemoryDeltaConflictException("Projection digest changed.");
        if (entry.Operation.Equals("RETRACT", StringComparison.OrdinalIgnoreCase) && old is null) throw new MemoryDeltaConflictException("Retract target missing.");
    }

    private static void ApplyEntry(SqliteConnection c, SqliteTransaction tx, MemoryDelta d, MemoryDeltaEntry entry, Projection? old, DateTimeOffset at)
    {
        if (entry.Operation.Equals("RETRACT", StringComparison.OrdinalIgnoreCase))
        {
            Execute(c, tx, "DELETE FROM memory_projection_entries WHERE workspace_id=$w AND projection=$p AND entity_id=$e;", ("$w", d.WorkspaceId), ("$p", entry.Projection), ("$e", entry.EntityId));
            return;
        }
        var digest = Hash(entry.PayloadJson);
        Execute(c, tx, "INSERT INTO memory_projection_entries(workspace_id,project_id,chapter_id,projection,entity_id,payload_json,digest,source_delta_id,revision,updated_at_utc) VALUES($w,$pr,$c,$p,$e,$j,$d,$s,$r,$at) ON CONFLICT(workspace_id,projection,entity_id) DO UPDATE SET project_id=excluded.project_id,chapter_id=excluded.chapter_id,payload_json=excluded.payload_json,digest=excluded.digest,source_delta_id=excluded.source_delta_id,revision=excluded.revision,updated_at_utc=excluded.updated_at_utc;",
            ("$w", d.WorkspaceId), ("$pr", d.ProjectId.ToString("D")), ("$c", d.ChapterId), ("$p", entry.Projection), ("$e", entry.EntityId), ("$j", entry.PayloadJson), ("$d", digest), ("$s", d.DeltaId.ToString("D")), ("$r", (old?.Revision ?? 0) + 1), ("$at", Text(at)));
    }

    private static string DraftHash(MemoryDeltaDraft d) => Hash(JsonSerializer.Serialize(new { d.ProjectId, d.WorkspaceId, d.ChapterId, d.GateId, d.LockedVersion, d.LockedDigest, d.Entries, d.Evidence, d.Actor }));
    private static string ControlHash(MemoryDeltaControlCommand c) => Hash(JsonSerializer.Serialize(new { c.WorkspaceId, c.DeltaId, c.ExpectedRevision, c.Actor }));
    private sealed record Receipt(string Workspace, Guid DeltaId, string Operation, string Fingerprint, string PayloadHash);
    private static Receipt? ReadReceipt(SqliteConnection c, SqliteTransaction tx, Guid id) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT workspace_id,delta_id,operation,request_fingerprint,payload_hash FROM memory_delta_requests WHERE request_id=$id;"; cmd.Parameters.AddWithValue("$id", id.ToString("D")); using var r = cmd.ExecuteReader(); return r.Read() ? new(r.GetString(0), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4)) : null; }
    private static void RequireReceipt(Receipt r, string op, string workspace, Guid deltaId, string fp, string hash) { if (r.Operation != op || r.Workspace != workspace || r.DeltaId != deltaId || r.Fingerprint != fp || r.PayloadHash != hash) throw new MemoryDeltaConflictException("Request id reused with different payload."); }
    private static void InsertReceipt(SqliteConnection c, SqliteTransaction tx, Guid request, string workspace, Guid deltaId, string op, string fp, string hash, long revision, Guid? message, DateTimeOffset at) => Execute(c, tx, "INSERT INTO memory_delta_requests(request_id,workspace_id,delta_id,operation,request_fingerprint,payload_hash,result_revision,message_id,created_at_utc) VALUES($q,$w,$d,$o,$f,$h,$r,$m,$at);", ("$q", request.ToString("D")), ("$w", workspace), ("$d", deltaId.ToString("D")), ("$o", op), ("$f", fp), ("$h", hash), ("$r", revision), ("$m", message is null ? DBNull.Value : message.Value.ToString("D")), ("$at", Text(at)));
    private static MemoryDelta? Read(SqliteConnection c, SqliteTransaction? tx, string workspace, Guid id) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT project_id,chapter_id,gate_id,locked_version,locked_digest,entries_json,evidence,actor,payload_hash,revision,status,message_id,created_at_utc,updated_at_utc FROM memory_deltas WHERE workspace_id=$w AND delta_id=$id;"; cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$id", id.ToString("D")); using var r = cmd.ExecuteReader(); if (!r.Read()) return null; return new(id, Guid.Parse(r.GetString(0)), workspace, r.GetString(1), Guid.Parse(r.GetString(2)), r.GetInt32(3), r.GetString(4), JsonSerializer.Deserialize<List<MemoryDeltaEntry>>(r.GetString(5)) ?? [], r.GetString(6), r.GetString(7), r.GetString(8), r.GetInt64(9), Enum.Parse<MemoryDeltaStatus>(r.GetString(10), true), r.IsDBNull(11) ? null : Guid.Parse(r.GetString(11)), Parse(r.GetString(12)), Parse(r.GetString(13))); }
    private static MemoryDelta Require(SqliteConnection c, SqliteTransaction tx, string workspace, Guid id) => Read(c, tx, workspace, id) ?? throw new KeyNotFoundException("Memory delta not found.");
    private static void UpdateStatus(SqliteConnection c, SqliteTransaction tx, MemoryDelta d, MemoryDeltaStatus status, long revision, Guid? message, DateTimeOffset at) => Execute(c, tx, "UPDATE memory_deltas SET status=$s,revision=$r,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND delta_id=$id;", ("$s", status.ToString().ToUpperInvariant()), ("$r", revision), ("$m", message is null ? DBNull.Value : message.Value.ToString("D")), ("$at", Text(at)), ("$w", d.WorkspaceId), ("$id", d.DeltaId.ToString("D")));
    private static void InsertOutbox(SqliteConnection c, SqliteTransaction tx, Guid message, string type, object payload, DateTimeOffset at) => Execute(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($m,$t,'1.0.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);", ("$m", message.ToString("D")), ("$t", type), ("$p", JsonSerializer.Serialize(payload)), ("$at", Text(at)));
    private static Guid MessageId(Guid request) => new(request.ToByteArray().Select((b, i) => (byte)(b ^ (i % 2 == 0 ? 0x55 : 0xAA))).ToArray());
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Text(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Name, p.Value); cmd.ExecuteNonQuery(); }
}

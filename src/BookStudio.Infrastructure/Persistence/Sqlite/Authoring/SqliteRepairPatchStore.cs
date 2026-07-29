using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteRepairPatchStore : IRepairPatchStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteRepairPatchStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<RepairPatchCreateResult> ProposeAsync(RepairPatchDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(d);
        var hash = Hash(JsonSerializer.Serialize(new { d.ProjectId, d.WorkspaceId, d.ArtifactId, d.ExpectedVersion, d.ExpectedDigest, d.Scope, d.Operations, d.Reason, d.Evidence, d.AuthorityType, d.AuthorityId, d.Actor }));
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, d.WorkspaceId, d.PatchId);
            if (existing is not null)
            {
                var receipt = ReadReceipt(c, tx, d.PatchId) ?? throw new RepairPatchConflictException("Proposal receipt missing.");
                RequireReceipt(receipt, "PROPOSE", d.WorkspaceId, d.PatchId, d.RequestFingerprint, hash);
                if (existing.PayloadHash != hash) throw new RepairPatchConflictException("Patch identity reused with different immutable payload.");
                return new RepairPatchCreateResult(existing, true);
            }
            ValidateAuthority(c, tx, d.WorkspaceId, d.ProjectId, d.AuthorityType, d.AuthorityId);
            Execute(c, tx, "INSERT INTO repair_patches(workspace_id,patch_id,project_id,artifact_id,expected_version,expected_digest,scope,operations_json,reason,evidence,authority_type,authority_id,actor,payload_hash,revision,status,result_digest,result_version,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,$v,$d,$s,$o,$r,$e,$t,$auth,$actor,$h,1,'PROPOSED',NULL,NULL,NULL,$at,$at);",
                ("$w", d.WorkspaceId), ("$id", d.PatchId.ToString("D")), ("$p", d.ProjectId.ToString("D")), ("$a", d.ArtifactId), ("$v", d.ExpectedVersion), ("$d", d.ExpectedDigest), ("$s", d.Scope), ("$o", JsonSerializer.Serialize(d.Operations)), ("$r", d.Reason), ("$e", d.Evidence), ("$t", d.AuthorityType.ToUpperInvariant()), ("$auth", d.AuthorityId.ToString("D")), ("$actor", d.Actor), ("$h", hash), ("$at", Text(at)));
            InsertReceipt(c, tx, d.PatchId, d.WorkspaceId, d.PatchId, "PROPOSE", d.RequestFingerprint, hash, 1, null, at);
            return new RepairPatchCreateResult(Require(c, tx, d.WorkspaceId, d.PatchId), false);
        }, ct);
    }

    public ValueTask<RepairPatch> ValidateAsync(RepairPatchControlCommand cmd, DateTimeOffset at, CancellationToken ct = default) => Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.PatchId, "VALIDATE", cmd.RequestFingerprint, ControlHash(cmd), cmd.ExpectedRevision, at, (c, tx, p) =>
    {
        if (p.Status != RepairPatchStatus.Proposed) throw new RepairPatchTransitionException("Only proposed patches can validate.");
        ValidateAuthority(c, tx, p.WorkspaceId, p.ProjectId, p.AuthorityType, p.AuthorityId);
        var target = ReadTarget(c, tx, p.WorkspaceId, p.ArtifactId) ?? throw new RepairPatchValidationException("Target artifact not found.");
        if (target.Version != p.ExpectedVersion || target.Digest != p.ExpectedDigest) return Terminal(c, tx, p, RepairPatchStatus.Stale, "editorial.repair-patch.stale", at, cmd.RequestId, cmd.Actor);
        ApplyOperations(target.Content, p.Scope, p.Operations, dryRun: true);
        UpdateStatus(c, tx, p, RepairPatchStatus.Validated, p.Revision + 1, null, null, null, at);
        return Require(c, tx, p.WorkspaceId, p.PatchId);
    });

    public ValueTask<RepairPatch> ApplyAsync(RepairPatchControlCommand cmd, DateTimeOffset at, CancellationToken ct = default) => Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.PatchId, "APPLY", cmd.RequestFingerprint, ControlHash(cmd), cmd.ExpectedRevision, at, (c, tx, p) =>
    {
        if (p.Status != RepairPatchStatus.Validated) throw new RepairPatchTransitionException("Only validated patches can apply.");
        ValidateAuthority(c, tx, p.WorkspaceId, p.ProjectId, p.AuthorityType, p.AuthorityId);
        var target = ReadTarget(c, tx, p.WorkspaceId, p.ArtifactId) ?? throw new RepairPatchValidationException("Target artifact not found.");
        if (target.Version != p.ExpectedVersion || target.Digest != p.ExpectedDigest) return Terminal(c, tx, p, RepairPatchStatus.Stale, "editorial.repair-patch.stale", at, cmd.RequestId, cmd.Actor);
        var patched = ApplyOperations(target.Content, p.Scope, p.Operations, dryRun: false);
        var canonical = patched.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var digest = Hash(canonical);
        var version = target.Version + 1;
        Execute(c, tx, "INSERT INTO repair_patch_history(history_id,workspace_id,patch_id,revision,status,artifact_version,artifact_digest,content_json,actor,occurred_at_utc) VALUES($h,$w,$p,$r,'APPLIED',$v,$d,$c,$a,$at);",
            ("$h", Guid.NewGuid().ToString("D")), ("$w", p.WorkspaceId), ("$p", p.PatchId.ToString("D")), ("$r", p.Revision + 1), ("$v", target.Version), ("$d", target.Digest), ("$c", target.Content), ("$a", cmd.Actor), ("$at", Text(at)));
        Execute(c, tx, "UPDATE repair_patch_targets SET version=$v,digest=$d,content_json=$c,updated_at_utc=$at WHERE workspace_id=$w AND artifact_id=$a;", ("$v", version), ("$d", digest), ("$c", canonical), ("$at", Text(at)), ("$w", p.WorkspaceId), ("$a", p.ArtifactId));
        var message = MessageId(cmd.RequestId);
        UpdateStatus(c, tx, p, RepairPatchStatus.Applied, p.Revision + 1, digest, version, message, at);
        InsertOutbox(c, tx, message, "editorial.repair-patch.applied", new { p.WorkspaceId, p.PatchId, p.ProjectId, p.ArtifactId, PreviousVersion = target.Version, ResultVersion = version, PreviousDigest = target.Digest, ResultDigest = digest, cmd.Actor }, at);
        return Require(c, tx, p.WorkspaceId, p.PatchId);
    });

    public ValueTask<RepairPatch> RejectAsync(RepairPatchDecisionCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new RepairPatchValidationException("Rejection reason is required.");
        var hash = Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.PatchId, cmd.ExpectedRevision, cmd.Reason, cmd.Actor }));
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.PatchId, "REJECT", cmd.RequestFingerprint, hash, cmd.ExpectedRevision, at, (c, tx, p) =>
        {
            if (p.Status is RepairPatchStatus.Applied or RepairPatchStatus.Rejected or RepairPatchStatus.Stale) throw new RepairPatchTransitionException("Terminal patch cannot be rejected.");
            return Terminal(c, tx, p, RepairPatchStatus.Rejected, "editorial.repair-patch.rejected", at, cmd.RequestId, cmd.Actor, cmd.Reason);
        });
    }

    public async ValueTask<RepairPatch?> GetAsync(string workspaceId, Guid patchId, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); using var c = _factory.OpenConnection(); return await Task.FromResult(Read(c, null, workspaceId, patchId)); }
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false); }

    private ValueTask<RepairPatch> Mutate(Guid requestId, string workspace, Guid patchId, string operation, string fingerprint, string payloadHash, long expectedRevision, DateTimeOffset at, Func<SqliteConnection, SqliteTransaction, RepairPatch, RepairPatch> action) =>
        _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var receipt = ReadReceipt(c, tx, requestId);
            if (receipt is not null) { RequireReceipt(receipt, operation, workspace, patchId, fingerprint, payloadHash); return Require(c, tx, workspace, patchId); }
            var patch = Require(c, tx, workspace, patchId);
            if (patch.Revision != expectedRevision) throw new RepairPatchConflictException("Stale revision.");
            var result = action(c, tx, patch);
            InsertReceipt(c, tx, requestId, workspace, patchId, operation, fingerprint, payloadHash, result.Revision, result.MessageId, at);
            return result;
        }, default);

    private static RepairPatch Terminal(SqliteConnection c, SqliteTransaction tx, RepairPatch p, RepairPatchStatus status, string eventType, DateTimeOffset at, Guid requestId, string actor, string? reason = null)
    {
        var message = MessageId(requestId);
        UpdateStatus(c, tx, p, status, p.Revision + 1, null, null, message, at);
        InsertOutbox(c, tx, message, eventType, new { p.WorkspaceId, p.PatchId, p.ProjectId, p.ArtifactId, Actor = actor, Reason = reason }, at);
        return Require(c, tx, p.WorkspaceId, p.PatchId);
    }

    private static JsonNode ApplyOperations(string content, string scope, IReadOnlyList<RepairOperation> operations, bool dryRun)
    {
        var root = JsonNode.Parse(content) ?? throw new RepairPatchValidationException("Target content is invalid JSON.");
        foreach (var op in operations)
        {
            if (!op.Path.StartsWith(scope, StringComparison.Ordinal) || op.Path.Contains("*", StringComparison.Ordinal)) throw new RepairPatchValidationException("Operation exceeds declared scope.");
            var segments = op.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) throw new RepairPatchValidationException("Root-wide operations are forbidden.");
            JsonNode current = root;
            for (var i = 0; i < segments.Length - 1; i++) current = current[segments[i]] ?? throw new RepairPatchConflictException("Operation path no longer exists.");
            var key = segments[^1];
            var existing = current[key]?.ToJsonString();
            if (op.ExpectedValue is not null && existing != JsonValue.Create(op.ExpectedValue)?.ToJsonString() && current[key]?.GetValue<string>() != op.ExpectedValue) throw new RepairPatchConflictException("Operation precondition changed.");
            if (dryRun) continue;
            switch (op.Kind)
            {
                case RepairOperationKind.ReplaceValue: if (current[key] is null) throw new RepairPatchConflictException("Replace target missing."); current[key] = op.NewValue; break;
                case RepairOperationKind.AddValue: if (current[key] is not null) throw new RepairPatchConflictException("Add target already exists."); current[key] = op.NewValue; break;
                case RepairOperationKind.RemoveValue: if (current is not JsonObject obj || !obj.Remove(key)) throw new RepairPatchConflictException("Remove target missing."); break;
                default: throw new RepairPatchValidationException("Unsupported repair operation.");
            }
        }
        return root;
    }

    private static void ValidateDraft(RepairPatchDraft d)
    {
        if (d.PatchId == Guid.Empty || d.ProjectId == Guid.Empty || d.AuthorityId == Guid.Empty || d.ExpectedVersion < 1 || string.IsNullOrWhiteSpace(d.WorkspaceId) || string.IsNullOrWhiteSpace(d.ArtifactId) || string.IsNullOrWhiteSpace(d.ExpectedDigest) || string.IsNullOrWhiteSpace(d.Scope) || d.Operations.Count == 0 || string.IsNullOrWhiteSpace(d.Reason) || string.IsNullOrWhiteSpace(d.Evidence) || string.IsNullOrWhiteSpace(d.Actor) || string.IsNullOrWhiteSpace(d.RequestFingerprint)) throw new RepairPatchValidationException("Complete repair patch is required.");
        if (d.AuthorityType.ToUpperInvariant() is not ("FINDING" or "AUDIT")) throw new RepairPatchValidationException("Authority type is invalid.");
        if (d.Operations.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != d.Operations.Count) throw new RepairPatchValidationException("Duplicate operation paths are forbidden.");
    }

    private static void ValidateAuthority(SqliteConnection c, SqliteTransaction tx, string workspace, Guid project, string type, Guid authority)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = type.ToUpperInvariant() == "AUDIT"
            ? "SELECT 1 FROM transition_audits WHERE workspace_id=$w AND project_id=$p AND audit_id=$id AND status='CLOSED';"
            : "SELECT 1 FROM transition_audits a, json_each(a.findings_json) f WHERE a.workspace_id=$w AND a.project_id=$p AND json_extract(f.value,'$.FindingId')=$id AND json_extract(f.value,'$.Decision')=0;";
        cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$p", project.ToString("D")); cmd.Parameters.AddWithValue("$id", authority.ToString("D"));
        if (cmd.ExecuteScalar() is null) throw new RepairPatchValidationException("Exact repair authority was not found.");
    }

    private sealed record Target(int Version, string Digest, string Content);
    private static Target? ReadTarget(SqliteConnection c, SqliteTransaction tx, string w, string artifact) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT version,digest,content_json FROM repair_patch_targets WHERE workspace_id=$w AND artifact_id=$a;"; cmd.Parameters.AddWithValue("$w", w); cmd.Parameters.AddWithValue("$a", artifact); using var r = cmd.ExecuteReader(); return r.Read() ? new(r.GetInt32(0), r.GetString(1), r.GetString(2)) : null; }
    private static RepairPatch? Read(SqliteConnection c, SqliteTransaction? tx, string w, Guid id) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT project_id,artifact_id,expected_version,expected_digest,scope,operations_json,reason,evidence,authority_type,authority_id,actor,payload_hash,revision,status,result_digest,result_version,message_id,created_at_utc,updated_at_utc FROM repair_patches WHERE workspace_id=$w AND patch_id=$id;"; cmd.Parameters.AddWithValue("$w", w); cmd.Parameters.AddWithValue("$id", id.ToString("D")); using var r = cmd.ExecuteReader(); if (!r.Read()) return null; return new(id, Guid.Parse(r.GetString(0)), w, r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), JsonSerializer.Deserialize<List<RepairOperation>>(r.GetString(5)) ?? [], r.GetString(6), r.GetString(7), r.GetString(8), Guid.Parse(r.GetString(9)), r.GetString(10), r.GetString(11), r.GetInt64(12), Enum.Parse<RepairPatchStatus>(r.GetString(13), true), r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetInt32(15), r.IsDBNull(16) ? null : Guid.Parse(r.GetString(16)), Parse(r.GetString(17)), Parse(r.GetString(18))); }
    private static RepairPatch Require(SqliteConnection c, SqliteTransaction tx, string w, Guid id) => Read(c, tx, w, id) ?? throw new KeyNotFoundException("Repair patch not found.");
    private sealed record Receipt(string Workspace, Guid PatchId, string Operation, string Fingerprint, string PayloadHash);
    private static Receipt? ReadReceipt(SqliteConnection c, SqliteTransaction tx, Guid id) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT workspace_id,patch_id,operation,request_fingerprint,payload_hash FROM repair_patch_requests WHERE request_id=$id;"; cmd.Parameters.AddWithValue("$id", id.ToString("D")); using var r = cmd.ExecuteReader(); return r.Read() ? new(r.GetString(0), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4)) : null; }
    private static void RequireReceipt(Receipt r, string op, string w, Guid id, string fp, string hash) { if (r.Operation != op || r.Workspace != w || r.PatchId != id || r.Fingerprint != fp || r.PayloadHash != hash) throw new RepairPatchConflictException("Request id reused with different immutable payload."); }
    private static void InsertReceipt(SqliteConnection c, SqliteTransaction tx, Guid q, string w, Guid id, string op, string fp, string hash, long rev, Guid? message, DateTimeOffset at) => Execute(c, tx, "INSERT INTO repair_patch_requests(request_id,workspace_id,patch_id,operation,request_fingerprint,payload_hash,result_revision,message_id,created_at_utc) VALUES($q,$w,$id,$o,$f,$h,$r,$m,$at);", ("$q", q.ToString("D")), ("$w", w), ("$id", id.ToString("D")), ("$o", op), ("$f", fp), ("$h", hash), ("$r", rev), ("$m", message is null ? DBNull.Value : message.Value.ToString("D")), ("$at", Text(at)));
    private static void UpdateStatus(SqliteConnection c, SqliteTransaction tx, RepairPatch p, RepairPatchStatus status, long revision, string? digest, int? version, Guid? message, DateTimeOffset at) => Execute(c, tx, "UPDATE repair_patches SET status=$s,revision=$r,result_digest=$d,result_version=$v,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND patch_id=$id;", ("$s", status.ToString().ToUpperInvariant()), ("$r", revision), ("$d", digest is null ? DBNull.Value : digest), ("$v", version is null ? DBNull.Value : version.Value), ("$m", message is null ? DBNull.Value : message.Value.ToString("D")), ("$at", Text(at)), ("$w", p.WorkspaceId), ("$id", p.PatchId.ToString("D")));
    private static void InsertOutbox(SqliteConnection c, SqliteTransaction tx, Guid message, string type, object payload, DateTimeOffset at) => Execute(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($m,$t,'1.0.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);", ("$m", message.ToString("D")), ("$t", type), ("$p", JsonSerializer.Serialize(payload)), ("$at", Text(at)));
    private static string ControlHash(RepairPatchControlCommand c) => Hash(JsonSerializer.Serialize(new { c.WorkspaceId, c.PatchId, c.ExpectedRevision, c.Actor }));
    private static Guid MessageId(Guid request) => new(request.ToByteArray().Select((b, i) => (byte)(b ^ (i % 2 == 0 ? 0x33 : 0xCC))).ToArray());
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Text(DateTimeOffset v) => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string v) => DateTimeOffset.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] p) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var x in p) cmd.Parameters.AddWithValue(x.Name, x.Value); cmd.ExecuteNonQuery(); }
}

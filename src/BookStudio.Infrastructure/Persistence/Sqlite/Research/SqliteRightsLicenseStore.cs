using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Research;

public sealed class SqliteRightsLicenseStore : IRightsLicenseStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteRightsLicenseStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<RightsLicenseCreateResult> CreateAsync(RightsLicenseDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(d);
        var hash = Hash(JsonSerializer.Serialize(d));
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, d.WorkspaceId, d.CaseId);
            if (existing is not null)
            {
                var receipt = ReadReceipt(c, tx, d.WorkspaceId, d.CaseId) ?? throw new RightsLicenseConflictException("Create receipt missing.");
                RequireReceipt(receipt, "CREATE", d.CaseId, d.RequestFingerprint, hash);
                return new RightsLicenseCreateResult(existing, true);
            }
            RequireAuthority(c, tx, d.WorkspaceId, d.ProjectId, d.BibliographyId, d.ExpectedBibliographyRevision, d.ExpectedBibliographyDigest);
            var message = MessageId(d.CaseId);
            Execute(c, tx, "INSERT INTO rights_license_cases(workspace_id,case_id,project_id,bibliography_id,expected_bibliography_revision,expected_bibliography_digest,asset_id,asset_kind,asset_reference,asset_digest,asset_version,rights_holder,actor,snapshot_json,revision,status,scope_json,valid_from_utc,valid_until_utc,restrictions_json,evidence,decision,decision_reason,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$b,$br,$bd,$asset,$kind,$ref,$digest,$version,$holder,$actor,$snapshot,1,'PROPOSED',NULL,NULL,NULL,'[]',NULL,NULL,NULL,$m,$at,$at);",
                ("$w", d.WorkspaceId), ("$id", d.CaseId.ToString("D")), ("$p", d.ProjectId.ToString("D")), ("$b", d.BibliographyId.ToString("D")), ("$br", d.ExpectedBibliographyRevision), ("$bd", d.ExpectedBibliographyDigest), ("$asset", d.AssetId.ToString("D")), ("$kind", d.AssetKind.ToString().ToUpperInvariant()), ("$ref", d.AssetReference), ("$digest", d.AssetDigest), ("$version", d.AssetVersion), ("$holder", d.RightsHolder), ("$actor", d.Actor), ("$snapshot", d.SnapshotJson), ("$m", message.ToString("D")), ("$at", Text(at)));
            InsertHistory(c, tx, d.WorkspaceId, d.CaseId, 1, "CREATE", d.Actor, null, new { d.AssetId, d.AssetKind }, at);
            InsertReceipt(c, tx, d.WorkspaceId, d.CaseId, d.CaseId, "CREATE", d.RequestFingerprint, hash, 1, message, at);
            InsertOutbox(c, tx, message, "rights.license.proposed", new { d.WorkspaceId, d.CaseId, d.AssetId }, at);
            return new RightsLicenseCreateResult(Require(c, tx, d.WorkspaceId, d.CaseId), false);
        }, ct);
    }

    public ValueTask<RightsLicenseCase> ValidateAsync(RightsLicenseValidateCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateScope(cmd.Scope, cmd.ValidFromUtc, cmd.ValidUntilUtc, cmd.Evidence);
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.CaseId, "VALIDATE", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(cmd)), cmd.ExpectedRevision, at, (c, tx, item) =>
        {
            if (!AuthorityMatches(c, tx, item)) return MarkStaleCore(c, tx, item, cmd.RequestId, "Citation bibliography authority drift.", cmd.Actor, at);
            if (item.Status is RightsLicenseStatus.Approved or RightsLicenseStatus.Rejected or RightsLicenseStatus.Revoked or RightsLicenseStatus.Expired or RightsLicenseStatus.Stale) throw new RightsLicenseTransitionException("Case cannot be validated from its current state.");
            return Advance(c, tx, item, RightsLicenseStatus.Validated, cmd.RequestId, "VALIDATE", cmd.Actor, cmd.Evidence, "rights.license.validated", at, scope: cmd.Scope, validFrom: cmd.ValidFromUtc, validUntil: cmd.ValidUntilUtc, restrictions: cmd.Restrictions, evidence: cmd.Evidence);
        }, ct);
    }

    public ValueTask<RightsLicenseCase> DecideAsync(RightsLicenseDecisionCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new RightsLicenseValidationException("Decision reason is required.");
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.CaseId, "DECIDE", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(cmd)), cmd.ExpectedRevision, at, (c, tx, item) =>
        {
            if (!AuthorityMatches(c, tx, item)) return MarkStaleCore(c, tx, item, cmd.RequestId, "Citation bibliography authority drift.", cmd.Actor, at);
            RightsLicenseStatus status;
            if (cmd.Decision is RightsLicenseDecision.Approve or RightsLicenseDecision.Reject)
            {
                if (item.Status != RightsLicenseStatus.Validated) throw new RightsLicenseTransitionException("Only validated cases can be approved or rejected.");
                if (cmd.Decision == RightsLicenseDecision.Approve && (item.Scope is null || string.IsNullOrWhiteSpace(item.Evidence))) throw new RightsLicenseTransitionException("Validated scope and evidence are required for approval.");
                status = cmd.Decision == RightsLicenseDecision.Approve ? RightsLicenseStatus.Approved : RightsLicenseStatus.Rejected;
            }
            else
            {
                if (item.Status != RightsLicenseStatus.Approved) throw new RightsLicenseTransitionException("Only approved rights can be revoked or expired.");
                status = cmd.Decision == RightsLicenseDecision.Revoke ? RightsLicenseStatus.Revoked : RightsLicenseStatus.Expired;
            }
            return Advance(c, tx, item, status, cmd.RequestId, "DECIDE", cmd.Actor, cmd.Reason, $"rights.license.{status.ToString().ToLowerInvariant()}", at, decision: cmd.Decision);
        }, ct);
    }

    public ValueTask<RightsLicenseCase> ReopenAsync(RightsLicenseReopenCommand cmd, DateTimeOffset at, CancellationToken ct = default) => Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.CaseId, "REOPEN", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(cmd)), cmd.ExpectedRevision, at, (c, tx, item) =>
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new RightsLicenseValidationException("Reopen reason is required.");
        if (item.Status is RightsLicenseStatus.Proposed or RightsLicenseStatus.Validated or RightsLicenseStatus.Stale) throw new RightsLicenseTransitionException("Case cannot be reopened from its current state.");
        return Advance(c, tx, item, RightsLicenseStatus.Proposed, cmd.RequestId, "REOPEN", cmd.Actor, cmd.Reason, "rights.license.reopened", at, clearValidation: true);
    }, ct);

    public ValueTask<RightsLicenseCase> MarkStaleAsync(RightsLicenseStaleCommand cmd, DateTimeOffset at, CancellationToken ct = default) => Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.CaseId, "STALE", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(cmd)), cmd.ExpectedRevision, at, (c, tx, item) => MarkStaleCore(c, tx, item, cmd.RequestId, cmd.Reason, cmd.Actor, at), ct);

    public async ValueTask<RightsLicenseCase?> GetAsync(string workspaceId, Guid caseId, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); using var c = _factory.OpenConnection(); return await Task.FromResult(Read(c, null, workspaceId, caseId)); }
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false); }

    private ValueTask<RightsLicenseCase> Mutate(Guid requestId, string workspace, Guid id, string action, string fingerprint, string hash, long expectedRevision, DateTimeOffset at, Func<SqliteConnection, SqliteTransaction, RightsLicenseCase, RightsLicenseCase> mutation, CancellationToken ct) => _queue.ExecuteInTransactionAsync((c, tx, token) => { token.ThrowIfCancellationRequested(); var receipt = ReadReceipt(c, tx, workspace, requestId); if (receipt is not null) { RequireReceipt(receipt, action, id, fingerprint, hash); return Require(c, tx, workspace, id); } var item = Require(c, tx, workspace, id); if (item.Revision != expectedRevision) throw new RightsLicenseConflictException("Stale revision."); var result = mutation(c, tx, item); InsertReceipt(c, tx, workspace, requestId, id, action, fingerprint, hash, result.Revision, result.MessageId, at); return result; }, ct);

    private static RightsLicenseCase Advance(SqliteConnection c, SqliteTransaction tx, RightsLicenseCase item, RightsLicenseStatus status, Guid request, string action, string actor, string reason, string eventType, DateTimeOffset at, LicenseScope? scope = null, DateTimeOffset? validFrom = null, DateTimeOffset? validUntil = null, IReadOnlyList<string>? restrictions = null, string? evidence = null, RightsLicenseDecision? decision = null, bool clearValidation = false)
    {
        var revision = item.Revision + 1; var message = MessageId(request);
        Execute(c, tx, "UPDATE rights_license_cases SET revision=$r,status=$s,scope_json=$scope,valid_from_utc=$from,valid_until_utc=$until,restrictions_json=$restrictions,evidence=$evidence,decision=$decision,decision_reason=$reason,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND case_id=$id;",
            ("$r", revision), ("$s", status.ToString().ToUpperInvariant()), ("$scope", clearValidation ? DBNull.Value : scope is null ? item.Scope is null ? DBNull.Value : JsonSerializer.Serialize(item.Scope) : JsonSerializer.Serialize(scope)), ("$from", clearValidation ? DBNull.Value : validFrom is null ? item.ValidFromUtc is null ? DBNull.Value : Text(item.ValidFromUtc.Value) : Text(validFrom.Value)), ("$until", clearValidation ? DBNull.Value : validUntil is null ? item.ValidUntilUtc is null ? DBNull.Value : Text(item.ValidUntilUtc.Value) : Text(validUntil.Value)), ("$restrictions", clearValidation ? "[]" : JsonSerializer.Serialize(restrictions ?? item.Restrictions)), ("$evidence", clearValidation ? DBNull.Value : evidence is null ? item.Evidence is null ? DBNull.Value : item.Evidence : evidence), ("$decision", decision is null ? DBNull.Value : decision.Value.ToString().ToUpperInvariant()), ("$reason", reason), ("$m", message.ToString("D")), ("$at", Text(at)), ("$w", item.WorkspaceId), ("$id", item.CaseId.ToString("D")));
        InsertHistory(c, tx, item.WorkspaceId, item.CaseId, revision, action, actor, reason, new { status, decision }, at); InsertOutbox(c, tx, message, eventType, new { item.WorkspaceId, item.CaseId, item.AssetId, Actor = actor }, at); return Require(c, tx, item.WorkspaceId, item.CaseId);
    }

    private static RightsLicenseCase MarkStaleCore(SqliteConnection c, SqliteTransaction tx, RightsLicenseCase item, Guid request, string reason, string actor, DateTimeOffset at) { if (string.IsNullOrWhiteSpace(reason)) throw new RightsLicenseValidationException("Stale reason is required."); if (item.Status is RightsLicenseStatus.Approved or RightsLicenseStatus.Rejected or RightsLicenseStatus.Revoked or RightsLicenseStatus.Expired) throw new RightsLicenseTransitionException("Final case cannot be marked stale."); return Advance(c, tx, item, RightsLicenseStatus.Stale, request, "STALE", actor, reason, "rights.license.stale", at); }

    private static void ValidateDraft(RightsLicenseDraft d) { if (d.CaseId == Guid.Empty || d.ProjectId == Guid.Empty || d.BibliographyId == Guid.Empty || d.AssetId == Guid.Empty || d.ExpectedBibliographyRevision < 1 || d.AssetVersion < 1 || string.IsNullOrWhiteSpace(d.WorkspaceId) || string.IsNullOrWhiteSpace(d.ExpectedBibliographyDigest) || string.IsNullOrWhiteSpace(d.AssetReference) || string.IsNullOrWhiteSpace(d.AssetDigest) || string.IsNullOrWhiteSpace(d.RightsHolder) || string.IsNullOrWhiteSpace(d.Actor) || string.IsNullOrWhiteSpace(d.SnapshotJson) || string.IsNullOrWhiteSpace(d.RequestFingerprint)) throw new RightsLicenseValidationException("Complete rights license draft is required."); }
    private static void ValidateScope(LicenseScope s, DateTimeOffset? from, DateTimeOffset? until, string evidence) { if (s is null || string.IsNullOrWhiteSpace(s.LicenseType) || s.Territories.Count == 0 || s.Languages.Count == 0 || s.Channels.Count == 0 || string.IsNullOrWhiteSpace(evidence)) throw new RightsLicenseValidationException("Complete license scope and evidence are required."); if (from is not null && until is not null && until <= from) throw new RightsLicenseValidationException("License validity window is invalid."); }
    private static void RequireAuthority(SqliteConnection c, SqliteTransaction tx, string workspace, Guid project, Guid id, long revision, string digest) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT project_id,revision,status FROM citation_bibliographies WHERE workspace_id=$w AND bibliography_id=$id;"; cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$id", id.ToString("D")); using var r = cmd.ExecuteReader(); if (!r.Read()) throw new RightsLicenseValidationException("Approved bibliography authority was not found."); var p = Guid.Parse(r.GetString(0)); var rev = r.GetInt64(1); var status = r.GetString(2); var actual = Hash($"{workspace}:{id:D}:{rev}:{status}"); if (p != project || rev != revision || status != "APPROVED" || actual != digest) throw new RightsLicenseValidationException("Bibliography authority is not exact and approved."); }
    private static bool AuthorityMatches(SqliteConnection c, SqliteTransaction tx, RightsLicenseCase item) { try { RequireAuthority(c, tx, item.WorkspaceId, item.ProjectId, item.BibliographyId, item.ExpectedBibliographyRevision, item.ExpectedBibliographyDigest); return true; } catch (RightsLicenseValidationException) { return false; } }

    private sealed record Receipt(Guid CaseId, string Action, string Fingerprint, string Hash);
    private static Receipt? ReadReceipt(SqliteConnection c, SqliteTransaction? tx, string w, Guid request) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT case_id,action,request_fingerprint,payload_hash FROM rights_license_receipts WHERE workspace_id=$w AND request_id=$r;"; cmd.Parameters.AddWithValue("$w", w); cmd.Parameters.AddWithValue("$r", request.ToString("D")); using var x = cmd.ExecuteReader(); return x.Read() ? new(Guid.Parse(x.GetString(0)), x.GetString(1), x.GetString(2), x.GetString(3)) : null; }
    private static void RequireReceipt(Receipt r, string action, Guid id, string fingerprint, string hash) { if (r.CaseId != id || r.Action != action || r.Fingerprint != fingerprint || r.Hash != hash) throw new RightsLicenseConflictException("Request id reused with different payload."); }
    private static RightsLicenseCase Require(SqliteConnection c, SqliteTransaction? tx, string w, Guid id) => Read(c, tx, w, id) ?? throw new RightsLicenseValidationException("Rights license case not found.");
    private static RightsLicenseCase? Read(SqliteConnection c, SqliteTransaction? tx, string w, Guid id) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT project_id,bibliography_id,expected_bibliography_revision,expected_bibliography_digest,asset_id,asset_kind,asset_reference,asset_digest,asset_version,rights_holder,actor,snapshot_json,revision,status,scope_json,valid_from_utc,valid_until_utc,restrictions_json,evidence,decision,decision_reason,message_id,created_at_utc,updated_at_utc FROM rights_license_cases WHERE workspace_id=$w AND case_id=$id;"; cmd.Parameters.AddWithValue("$w", w); cmd.Parameters.AddWithValue("$id", id.ToString("D")); using var r = cmd.ExecuteReader(); if (!r.Read()) return null; return new(id, Guid.Parse(r.GetString(0)), w, Guid.Parse(r.GetString(1)), r.GetInt64(2), r.GetString(3), Guid.Parse(r.GetString(4)), Enum.Parse<AssetKind>(r.GetString(5), true), r.GetString(6), r.GetString(7), r.GetInt32(8), r.GetString(9), r.GetString(10), r.GetString(11), r.GetInt64(12), Enum.Parse<RightsLicenseStatus>(r.GetString(13), true), r.IsDBNull(14) ? null : JsonSerializer.Deserialize<LicenseScope>(r.GetString(14)), r.IsDBNull(15) ? null : DateTimeOffset.Parse(r.GetString(15), CultureInfo.InvariantCulture), r.IsDBNull(16) ? null : DateTimeOffset.Parse(r.GetString(16), CultureInfo.InvariantCulture), JsonSerializer.Deserialize<List<string>>(r.GetString(17)) ?? [], r.IsDBNull(18) ? null : r.GetString(18), r.IsDBNull(19) ? null : Enum.Parse<RightsLicenseDecision>(r.GetString(19), true), r.IsDBNull(20) ? null : r.GetString(20), r.IsDBNull(21) ? null : Guid.Parse(r.GetString(21)), DateTimeOffset.Parse(r.GetString(22), CultureInfo.InvariantCulture), DateTimeOffset.Parse(r.GetString(23), CultureInfo.InvariantCulture)); }
    private static void InsertHistory(SqliteConnection c, SqliteTransaction tx, string w, Guid id, long rev, string action, string actor, string? reason, object payload, DateTimeOffset at) => Execute(c, tx, "INSERT INTO rights_license_history(workspace_id,case_id,revision,action,actor,reason,payload_json,occurred_at_utc) VALUES($w,$id,$r,$a,$actor,$reason,$p,$at);", ("$w", w), ("$id", id.ToString("D")), ("$r", rev), ("$a", action), ("$actor", actor), ("$reason", reason is null ? DBNull.Value : reason), ("$p", JsonSerializer.Serialize(payload)), ("$at", Text(at)));
    private static void InsertReceipt(SqliteConnection c, SqliteTransaction tx, string w, Guid request, Guid id, string action, string fingerprint, string hash, long rev, Guid? message, DateTimeOffset at) => Execute(c, tx, "INSERT INTO rights_license_receipts(workspace_id,request_id,case_id,action,request_fingerprint,payload_hash,resulting_revision,message_id,created_at_utc) VALUES($w,$r,$id,$a,$f,$h,$v,$m,$at);", ("$w", w), ("$r", request.ToString("D")), ("$id", id.ToString("D")), ("$a", action), ("$f", fingerprint), ("$h", hash), ("$v", rev), ("$m", message is null ? DBNull.Value : message.Value.ToString("D")), ("$at", Text(at)));
    private static void InsertOutbox(SqliteConnection c, SqliteTransaction tx, Guid message, string type, object payload, DateTimeOffset at) => Execute(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($id,$t,'1.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);", ("$id", message.ToString("D")), ("$t", type), ("$p", JsonSerializer.Serialize(payload)), ("$at", Text(at)));
    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] values) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var (n, v) in values) cmd.Parameters.AddWithValue(n, v); cmd.ExecuteNonQuery(); }
    private static Guid MessageId(Guid request) => new(SHA256.HashData(request.ToByteArray()).AsSpan(0, 16));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Text(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

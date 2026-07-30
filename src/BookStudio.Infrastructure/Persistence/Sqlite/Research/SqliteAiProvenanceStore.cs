using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Research;

public sealed class SqliteAiProvenanceStore : IAiProvenanceStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteAiProvenanceStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<AiProvenanceCreateResult> CreateAsync(AiProvenanceDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(d);
        var hash = Hash(JsonSerializer.Serialize(d));
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, d.WorkspaceId, d.RecordId);
            if (existing is not null)
            {
                var receipt = ReadReceipt(c, tx, d.WorkspaceId, d.RecordId) ?? throw new AiProvenanceConflictException("Create receipt missing.");
                RequireReceipt(receipt, "CREATE", d.RecordId, d.RequestFingerprint, hash);
                return new AiProvenanceCreateResult(existing, true);
            }

            RequireAuthority(c, tx, d.WorkspaceId, d.ProjectId, d.RightsLicenseCaseId, d.ExpectedRightsRevision, d.ExpectedRightsDigest, d.AssetId, d.AssetDigest, d.AssetVersion);
            var message = MessageId(d.RecordId);
            Execute(c, tx, "INSERT INTO ai_provenance_records(workspace_id,record_id,project_id,rights_license_case_id,expected_rights_revision,expected_rights_digest,asset_id,asset_kind,asset_reference,asset_digest,asset_version,actor,snapshot_json,revision,status,classification,provider,model,model_version,prompt_reference,human_transformations,ai_contribution_percent,evidence,decision,decision_reason,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$rights,$rr,$rd,$asset,$kind,$ref,$digest,$version,$actor,$snapshot,1,'PROPOSED',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,$m,$at,$at);",
                ("$w", d.WorkspaceId), ("$id", d.RecordId.ToString("D")), ("$p", d.ProjectId.ToString("D")), ("$rights", d.RightsLicenseCaseId.ToString("D")), ("$rr", d.ExpectedRightsRevision), ("$rd", d.ExpectedRightsDigest), ("$asset", d.AssetId.ToString("D")), ("$kind", d.AssetKind.ToString().ToUpperInvariant()), ("$ref", d.AssetReference), ("$digest", d.AssetDigest), ("$version", d.AssetVersion), ("$actor", d.Actor), ("$snapshot", d.SnapshotJson), ("$m", message.ToString("D")), ("$at", Text(at)));
            InsertHistory(c, tx, d.WorkspaceId, d.RecordId, 1, "CREATE", d.Actor, null, new { d.AssetId, d.AssetKind }, at);
            InsertReceipt(c, tx, d.WorkspaceId, d.RecordId, d.RecordId, "CREATE", d.RequestFingerprint, hash, 1, message, at);
            InsertOutbox(c, tx, message, "ai.provenance.proposed", new { d.WorkspaceId, d.RecordId, d.AssetId }, at);
            return new AiProvenanceCreateResult(Require(c, tx, d.WorkspaceId, d.RecordId), false);
        }, ct);
    }

    public ValueTask<AiProvenanceRecord> EvaluateAsync(AiProvenanceEvaluateCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateEvaluation(cmd);
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.RecordId, "EVALUATE", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(cmd)), cmd.ExpectedRevision, at, (c, tx, item) =>
        {
            if (!AuthorityMatches(c, tx, item)) return MarkStaleCore(c, tx, item, cmd.RequestId, "Rights authority drift.", cmd.Actor, at);
            if (item.Status is AiProvenanceStatus.Approved or AiProvenanceStatus.Rejected or AiProvenanceStatus.Revoked or AiProvenanceStatus.Stale) throw new AiProvenanceTransitionException("Record cannot be evaluated from its current state.");
            ReplaceDisclosures(c, tx, item.WorkspaceId, item.RecordId, cmd.Disclosures);
            return Advance(c, tx, item, AiProvenanceStatus.Evaluated, cmd.RequestId, "EVALUATE", cmd.Actor, cmd.Evidence, "ai.provenance.evaluated", at, cmd.Classification, cmd.Provider, cmd.Model, cmd.ModelVersion, cmd.PromptReference, cmd.HumanTransformations, cmd.AiContributionPercent, cmd.Evidence);
        }, ct);
    }

    public ValueTask<AiProvenanceRecord> DecideAsync(AiProvenanceDecisionCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new AiProvenanceValidationException("Decision reason is required.");
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.RecordId, "DECIDE", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(cmd)), cmd.ExpectedRevision, at, (c, tx, item) =>
        {
            if (!AuthorityMatches(c, tx, item)) return MarkStaleCore(c, tx, item, cmd.RequestId, "Rights authority drift.", cmd.Actor, at);
            var status = cmd.Decision switch
            {
                AiProvenanceDecision.Approve when item.Status == AiProvenanceStatus.Evaluated && item.Disclosures.Count > 0 && item.Disclosures.All(x => x.PolicyCompliant) => AiProvenanceStatus.Approved,
                AiProvenanceDecision.Reject when item.Status == AiProvenanceStatus.Evaluated => AiProvenanceStatus.Rejected,
                AiProvenanceDecision.ReturnToRepair when item.Status == AiProvenanceStatus.Evaluated => AiProvenanceStatus.RepairRequired,
                AiProvenanceDecision.Revoke when item.Status == AiProvenanceStatus.Approved => AiProvenanceStatus.Revoked,
                _ => throw new AiProvenanceTransitionException("Decision is not valid for the current state.")
            };
            return Advance(c, tx, item, status, cmd.RequestId, "DECIDE", cmd.Actor, cmd.Reason, $"ai.provenance.{status.ToString().ToLowerInvariant()}", at, decision: cmd.Decision);
        }, ct);
    }

    public ValueTask<AiProvenanceRecord> ReopenAsync(AiProvenanceReopenCommand cmd, DateTimeOffset at, CancellationToken ct = default) => Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.RecordId, "REOPEN", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(cmd)), cmd.ExpectedRevision, at, (c, tx, item) =>
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new AiProvenanceValidationException("Reopen reason is required.");
        if (item.Status is AiProvenanceStatus.Proposed or AiProvenanceStatus.Evaluated or AiProvenanceStatus.Stale) throw new AiProvenanceTransitionException("Record cannot be reopened from its current state.");
        DeleteDisclosures(c, tx, item.WorkspaceId, item.RecordId);
        return Advance(c, tx, item, AiProvenanceStatus.Proposed, cmd.RequestId, "REOPEN", cmd.Actor, cmd.Reason, "ai.provenance.reopened", at, clearEvaluation: true);
    }, ct);

    public ValueTask<AiProvenanceRecord> MarkStaleAsync(AiProvenanceStaleCommand cmd, DateTimeOffset at, CancellationToken ct = default) => Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.RecordId, "STALE", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(cmd)), cmd.ExpectedRevision, at, (c, tx, item) => MarkStaleCore(c, tx, item, cmd.RequestId, cmd.Reason, cmd.Actor, at), ct);

    public async ValueTask<AiProvenanceRecord?> GetAsync(string workspaceId, Guid recordId, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); using var c = _factory.OpenConnection(); return await Task.FromResult(Read(c, null, workspaceId, recordId)); }
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false); }

    private ValueTask<AiProvenanceRecord> Mutate(Guid requestId, string workspace, Guid id, string action, string fingerprint, string hash, long expectedRevision, DateTimeOffset at, Func<SqliteConnection, SqliteTransaction, AiProvenanceRecord, AiProvenanceRecord> mutation, CancellationToken ct) => _queue.ExecuteInTransactionAsync((c, tx, token) => { token.ThrowIfCancellationRequested(); var receipt = ReadReceipt(c, tx, workspace, requestId); if (receipt is not null) { RequireReceipt(receipt, action, id, fingerprint, hash); return Require(c, tx, workspace, id); } var item = Require(c, tx, workspace, id); if (item.Revision != expectedRevision) throw new AiProvenanceConflictException("Stale revision."); var result = mutation(c, tx, item); InsertReceipt(c, tx, workspace, requestId, id, action, fingerprint, hash, result.Revision, result.MessageId, at); return result; }, ct);

    private static AiProvenanceRecord Advance(SqliteConnection c, SqliteTransaction tx, AiProvenanceRecord item, AiProvenanceStatus status, Guid request, string action, string actor, string reason, string eventType, DateTimeOffset at, AiProvenanceClassification? classification = null, string? provider = null, string? model = null, string? modelVersion = null, string? prompt = null, string? transformations = null, decimal? percent = null, string? evidence = null, AiProvenanceDecision? decision = null, bool clearEvaluation = false)
    {
        var revision = item.Revision + 1; var message = MessageId(request);
        Execute(c, tx, "UPDATE ai_provenance_records SET revision=$r,status=$s,classification=$classification,provider=$provider,model=$model,model_version=$modelVersion,prompt_reference=$prompt,human_transformations=$transformations,ai_contribution_percent=$percent,evidence=$evidence,decision=$decision,decision_reason=$reason,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND record_id=$id;",
            ("$r", revision), ("$s", status.ToString().ToUpperInvariant()), ("$classification", clearEvaluation ? DBNull.Value : classification is null ? item.Classification is null ? DBNull.Value : item.Classification.Value.ToString().ToUpperInvariant() : classification.Value.ToString().ToUpperInvariant()), ("$provider", clearEvaluation ? DBNull.Value : provider ?? (object?)item.Provider ?? DBNull.Value), ("$model", clearEvaluation ? DBNull.Value : model ?? (object?)item.Model ?? DBNull.Value), ("$modelVersion", clearEvaluation ? DBNull.Value : modelVersion ?? (object?)item.ModelVersion ?? DBNull.Value), ("$prompt", clearEvaluation ? DBNull.Value : prompt ?? (object?)item.PromptReference ?? DBNull.Value), ("$transformations", clearEvaluation ? DBNull.Value : transformations ?? (object?)item.HumanTransformations ?? DBNull.Value), ("$percent", clearEvaluation ? DBNull.Value : percent is null ? item.AiContributionPercent is null ? DBNull.Value : item.AiContributionPercent.Value : percent.Value), ("$evidence", clearEvaluation ? DBNull.Value : evidence ?? (object?)item.Evidence ?? DBNull.Value), ("$decision", decision is null ? DBNull.Value : decision.Value.ToString().ToUpperInvariant()), ("$reason", reason), ("$m", message.ToString("D")), ("$at", Text(at)), ("$w", item.WorkspaceId), ("$id", item.RecordId.ToString("D")));
        InsertHistory(c, tx, item.WorkspaceId, item.RecordId, revision, action, actor, reason, new { status, decision }, at); InsertOutbox(c, tx, message, eventType, new { item.WorkspaceId, item.RecordId, item.AssetId, Actor = actor }, at); return Require(c, tx, item.WorkspaceId, item.RecordId);
    }

    private static AiProvenanceRecord MarkStaleCore(SqliteConnection c, SqliteTransaction tx, AiProvenanceRecord item, Guid request, string reason, string actor, DateTimeOffset at) { if (string.IsNullOrWhiteSpace(reason)) throw new AiProvenanceValidationException("Stale reason is required."); if (item.Status is AiProvenanceStatus.Approved or AiProvenanceStatus.Rejected or AiProvenanceStatus.Revoked) throw new AiProvenanceTransitionException("Final record cannot be marked stale."); return Advance(c, tx, item, AiProvenanceStatus.Stale, request, "STALE", actor, reason, "ai.provenance.stale", at); }
    private static void ValidateDraft(AiProvenanceDraft d) { if (d.RecordId == Guid.Empty || d.ProjectId == Guid.Empty || d.RightsLicenseCaseId == Guid.Empty || d.AssetId == Guid.Empty || d.ExpectedRightsRevision < 1 || d.AssetVersion < 1 || string.IsNullOrWhiteSpace(d.WorkspaceId) || string.IsNullOrWhiteSpace(d.ExpectedRightsDigest) || string.IsNullOrWhiteSpace(d.AssetReference) || string.IsNullOrWhiteSpace(d.AssetDigest) || string.IsNullOrWhiteSpace(d.Actor) || string.IsNullOrWhiteSpace(d.SnapshotJson) || string.IsNullOrWhiteSpace(d.RequestFingerprint)) throw new AiProvenanceValidationException("Complete AI provenance draft is required."); }
    private static void ValidateEvaluation(AiProvenanceEvaluateCommand c) { if (string.IsNullOrWhiteSpace(c.PromptReference) || string.IsNullOrWhiteSpace(c.HumanTransformations) || string.IsNullOrWhiteSpace(c.Evidence) || c.Disclosures.Count == 0 || c.Disclosures.Any(x => x.DisclosureId == Guid.Empty || string.IsNullOrWhiteSpace(x.Channel) || string.IsNullOrWhiteSpace(x.Locale) || string.IsNullOrWhiteSpace(x.Format) || string.IsNullOrWhiteSpace(x.PolicyVersion) || string.IsNullOrWhiteSpace(x.Text) || string.IsNullOrWhiteSpace(x.Evidence)) || c.AiContributionPercent is < 0 or > 100) throw new AiProvenanceValidationException("Complete classification, disclosures and evidence are required."); if (c.Classification != AiProvenanceClassification.HumanCreated && (string.IsNullOrWhiteSpace(c.Provider) || string.IsNullOrWhiteSpace(c.Model))) throw new AiProvenanceValidationException("AI provider and model are required."); }
    private static void RequireAuthority(SqliteConnection c, SqliteTransaction tx, string workspace, Guid project, Guid id, long revision, string digest, Guid assetId, string assetDigest, int assetVersion) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT project_id,revision,status,asset_id,asset_digest,asset_version FROM rights_license_cases WHERE workspace_id=$w AND case_id=$id;"; cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$id", id.ToString("D")); using var r = cmd.ExecuteReader(); if (!r.Read()) throw new AiProvenanceValidationException("Approved rights authority was not found."); var p = Guid.Parse(r.GetString(0)); var rev = r.GetInt64(1); var status = r.GetString(2); var actual = Hash($"{workspace}:{id:D}:{rev}:{status}"); if (p != project || rev != revision || status != "APPROVED" || actual != digest || Guid.Parse(r.GetString(3)) != assetId || r.GetString(4) != assetDigest || r.GetInt32(5) != assetVersion) throw new AiProvenanceValidationException("Rights authority is not exact and approved."); }
    private static bool AuthorityMatches(SqliteConnection c, SqliteTransaction tx, AiProvenanceRecord item) { try { RequireAuthority(c, tx, item.WorkspaceId, item.ProjectId, item.RightsLicenseCaseId, item.ExpectedRightsRevision, item.ExpectedRightsDigest, item.AssetId, item.AssetDigest, item.AssetVersion); return true; } catch (AiProvenanceValidationException) { return false; } }

    private sealed record Receipt(Guid RecordId, string Action, string Fingerprint, string Hash);
    private static Receipt? ReadReceipt(SqliteConnection c, SqliteTransaction? tx, string w, Guid request) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT record_id,operation,request_fingerprint,payload_hash FROM ai_provenance_receipts WHERE workspace_id=$w AND request_id=$r;"; cmd.Parameters.AddWithValue("$w", w); cmd.Parameters.AddWithValue("$r", request.ToString("D")); using var x = cmd.ExecuteReader(); return x.Read() ? new(Guid.Parse(x.GetString(0)), x.GetString(1), x.GetString(2), x.GetString(3)) : null; }
    private static void RequireReceipt(Receipt r, string action, Guid id, string fingerprint, string hash) { if (r.RecordId != id || r.Action != action || r.Fingerprint != fingerprint || r.Hash != hash) throw new AiProvenanceConflictException("Request id reused with different payload."); }
    private static AiProvenanceRecord Require(SqliteConnection c, SqliteTransaction? tx, string w, Guid id) => Read(c, tx, w, id) ?? throw new AiProvenanceValidationException("AI provenance record not found.");
    private static AiProvenanceRecord? Read(SqliteConnection c, SqliteTransaction? tx, string w, Guid id) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT project_id,rights_license_case_id,expected_rights_revision,expected_rights_digest,asset_id,asset_kind,asset_reference,asset_digest,asset_version,actor,snapshot_json,revision,status,classification,provider,model,model_version,prompt_reference,human_transformations,ai_contribution_percent,evidence,decision,decision_reason,message_id,created_at_utc,updated_at_utc FROM ai_provenance_records WHERE workspace_id=$w AND record_id=$id;"; cmd.Parameters.AddWithValue("$w", w); cmd.Parameters.AddWithValue("$id", id.ToString("D")); using var r = cmd.ExecuteReader(); if (!r.Read()) return null; var disclosures = ReadDisclosures(c, tx, w, id); return new(id, Guid.Parse(r.GetString(0)), w, Guid.Parse(r.GetString(1)), r.GetInt64(2), r.GetString(3), Guid.Parse(r.GetString(4)), Enum.Parse<AssetKind>(r.GetString(5), true), r.GetString(6), r.GetString(7), r.GetInt32(8), r.GetString(9), r.GetString(10), r.GetInt64(11), Enum.Parse<AiProvenanceStatus>(r.GetString(12), true), r.IsDBNull(13) ? null : Enum.Parse<AiProvenanceClassification>(r.GetString(13), true), r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetString(15), r.IsDBNull(16) ? null : r.GetString(16), r.IsDBNull(17) ? null : r.GetString(17), r.IsDBNull(18) ? null : r.GetString(18), r.IsDBNull(19) ? null : Convert.ToDecimal(r.GetDouble(19), CultureInfo.InvariantCulture), disclosures, r.IsDBNull(20) ? null : r.GetString(20), r.IsDBNull(21) ? null : Enum.Parse<AiProvenanceDecision>(r.GetString(21), true), r.IsDBNull(22) ? null : r.GetString(22), r.IsDBNull(23) ? null : Guid.Parse(r.GetString(23)), DateTimeOffset.Parse(r.GetString(24), CultureInfo.InvariantCulture), DateTimeOffset.Parse(r.GetString(25), CultureInfo.InvariantCulture)); }
    private static List<AiDisclosure> ReadDisclosures(SqliteConnection c, SqliteTransaction? tx, string w, Guid id) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT disclosure_id,channel,locale,format,policy_version,text,policy_compliant,evidence FROM ai_provenance_disclosures WHERE workspace_id=$w AND record_id=$id ORDER BY disclosure_id;"; cmd.Parameters.AddWithValue("$w", w); cmd.Parameters.AddWithValue("$id", id.ToString("D")); using var r = cmd.ExecuteReader(); var result = new List<AiDisclosure>(); while (r.Read()) result.Add(new(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6) == 1, r.GetString(7))); return result; }
    private static void ReplaceDisclosures(SqliteConnection c, SqliteTransaction tx, string w, Guid id, IReadOnlyList<AiDisclosureDraft> values) { DeleteDisclosures(c, tx, w, id); foreach (var d in values) Execute(c, tx, "INSERT INTO ai_provenance_disclosures(workspace_id,record_id,disclosure_id,channel,locale,format,policy_version,text,policy_compliant,evidence) VALUES($w,$id,$d,$channel,$locale,$format,$policy,$text,$ok,$evidence);", ("$w", w), ("$id", id.ToString("D")), ("$d", d.DisclosureId.ToString("D")), ("$channel", d.Channel), ("$locale", d.Locale), ("$format", d.Format), ("$policy", d.PolicyVersion), ("$text", d.Text), ("$ok", d.PolicyCompliant ? 1 : 0), ("$evidence", d.Evidence)); }
    private static void DeleteDisclosures(SqliteConnection c, SqliteTransaction tx, string w, Guid id) => Execute(c, tx, "DELETE FROM ai_provenance_disclosures WHERE workspace_id=$w AND record_id=$id;", ("$w", w), ("$id", id.ToString("D")));
    private static void InsertHistory(SqliteConnection c, SqliteTransaction tx, string w, Guid id, long rev, string action, string actor, string? reason, object payload, DateTimeOffset at) => Execute(c, tx, "INSERT INTO ai_provenance_history(workspace_id,record_id,revision,transition,actor,reason,payload_json,occurred_at_utc) VALUES($w,$id,$r,$a,$actor,$reason,$p,$at);", ("$w", w), ("$id", id.ToString("D")), ("$r", rev), ("$a", action), ("$actor", actor), ("$reason", reason is null ? DBNull.Value : reason), ("$p", JsonSerializer.Serialize(payload)), ("$at", Text(at)));
    private static void InsertReceipt(SqliteConnection c, SqliteTransaction tx, string w, Guid request, Guid id, string action, string fingerprint, string hash, long rev, Guid? message, DateTimeOffset at) => Execute(c, tx, "INSERT INTO ai_provenance_receipts(workspace_id,request_id,record_id,operation,request_fingerprint,payload_hash,result_revision,message_id,created_at_utc) VALUES($w,$r,$id,$a,$f,$h,$v,$m,$at);", ("$w", w), ("$r", request.ToString("D")), ("$id", id.ToString("D")), ("$a", action), ("$f", fingerprint), ("$h", hash), ("$v", rev), ("$m", message is null ? DBNull.Value : message.Value.ToString("D")), ("$at", Text(at)));
    private static void InsertOutbox(SqliteConnection c, SqliteTransaction tx, Guid message, string type, object payload, DateTimeOffset at) => Execute(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($id,$t,'1.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);", ("$id", message.ToString("D")), ("$t", type), ("$p", JsonSerializer.Serialize(payload)), ("$at", Text(at)));
    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] values) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var (n, v) in values) cmd.Parameters.AddWithValue(n, v); cmd.ExecuteNonQuery(); }
    private static Guid MessageId(Guid request) => new(SHA256.HashData(request.ToByteArray()).AsSpan(0, 16));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Text(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

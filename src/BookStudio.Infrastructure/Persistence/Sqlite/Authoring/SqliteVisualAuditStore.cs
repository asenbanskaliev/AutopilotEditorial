using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteVisualAuditStore : IVisualAuditStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteVisualAuditStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async ValueTask<VisualAuditSubmissionResult> SubmitAsync(VisualAuditRequest request, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(request);
        await _gate.WaitAsync(ct);
        try
        {
            var payload = Hash(JsonSerializer.Serialize(request));
            var existing = Load(request.WorkspaceId, request.AuditId);
            if (existing is not null)
            {
                RequireReceipt(request.WorkspaceId, request.AuditId, request.AuditId, request.RequestFingerprint, payload);
                return new(existing, true);
            }
            RequireAuthorities(request);
            var message = MessageId(request.AuditId, 1);
            var state = new VisualAuditState(request.AuditId, request.ProjectId, request.WorkspaceId,
                request.AssetId, request.ExpectedAssetRevision, request.ExpectedAssetDigest,
                request.VisualBriefId, request.ExpectedVisualBriefRevision, request.ExpectedVisualBriefDigest,
                request.AdapterRequestId, request.AdapterEvidenceDigest, request.PolicyId, request.PolicyVersion,
                request.RequestedChecks, [], [], [], [], VisualAuditAggregateOutcome.Pending,
                VisualAuditStatus.Submitted, 1, message, at, at);

            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            Exec(connection, tx, "INSERT INTO visual_audits(workspace_id,audit_id,project_id,asset_id,expected_asset_revision,expected_asset_digest,visual_brief_id,expected_visual_brief_revision,expected_visual_brief_digest,adapter_request_id,adapter_evidence_digest,policy_id,policy_version,requested_checks_json,outcome,status,request_fingerprint,revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,$ar,$ad,$b,$br,$bd,$ir,$ie,$pi,$pv,$c,'PENDING','SUBMITTED',$f,1,$m,$at,$at)",
                ("$w",request.WorkspaceId),("$id",request.AuditId.ToString("D")),("$p",request.ProjectId.ToString("D")),
                ("$a",request.AssetId.ToString("D")),("$ar",request.ExpectedAssetRevision),("$ad",request.ExpectedAssetDigest),
                ("$b",request.VisualBriefId.ToString("D")),("$br",request.ExpectedVisualBriefRevision),("$bd",request.ExpectedVisualBriefDigest),
                ("$ir",request.AdapterRequestId is null ? DBNull.Value : request.AdapterRequestId.Value.ToString("D")),
                ("$ie",request.AdapterEvidenceDigest is null ? DBNull.Value : request.AdapterEvidenceDigest),
                ("$pi",request.PolicyId),("$pv",request.PolicyVersion),("$c",JsonSerializer.Serialize(request.RequestedChecks)),
                ("$f",request.RequestFingerprint),("$m",message.ToString("D")),("$at",at.ToString("O")));
            History(connection, tx, state, "SUBMIT", request.Actor, "visual audit submitted", at);
            Receipt(connection, tx, request.WorkspaceId, request.AuditId, request.AuditId, request.RequestFingerprint, payload, state, at);
            Outbox(connection, tx, state, "visual.audit.submitted", at);
            tx.Commit();
            return new(state, false);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<VisualAuditState> RecordChecksAsync(VisualAuditCheckBatch batch, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(batch.WorkspaceId, batch.AuditId, batch.ExpectedRevision,
            DeterministicGuid(batch.AuditId, $"checks:{batch.ExecutionId:D}"), batch.RequestFingerprint,
            Hash(JsonSerializer.Serialize(batch)), "CHECKS", batch.Actor, "audit checks recorded", at, state =>
            {
                if (state.Status is not (VisualAuditStatus.Submitted or VisualAuditStatus.Running))
                    throw new VisualAuditTransitionException("Audit cannot accept checks in its current state.");
                if (batch.Checks.Count == 0 || batch.Checks.Select(x => x.Kind).Distinct().Count() != batch.Checks.Count)
                    throw new VisualAuditValidationException("A non-empty unique check batch is required.");
                return state with { Checks = batch.Checks.ToArray(), Status = VisualAuditStatus.Running,
                    Revision = state.Revision + 1, MessageId = MessageId(state.AuditId, state.Revision + 1), UpdatedAtUtc = at };
            }, (connection, tx, _) => PersistChecks(connection, tx, batch), ct);

    public ValueTask<VisualAuditState> CompleteAsync(VisualAuditCompletion completion, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(completion.WorkspaceId, completion.AuditId, completion.ExpectedRevision,
            DeterministicGuid(completion.AuditId, $"complete:{completion.AggregationEvidenceDigest}"), completion.RequestFingerprint,
            Hash(JsonSerializer.Serialize(completion)), "COMPLETE", completion.Actor, completion.AggregationEvidence, at, state =>
            {
                RequireText(completion.AggregationEvidence, completion.AggregationEvidenceDigest);
                if (state.Status != VisualAuditStatus.Running) throw new VisualAuditTransitionException("Only running audits can complete.");
                if (!state.RequestedChecks.SetEquals(state.Checks.Select(x => x.Kind)))
                    throw new VisualAuditValidationException("Complete policy check coverage is required.");
                var status = completion.Outcome switch
                {
                    VisualAuditAggregateOutcome.Pass => VisualAuditStatus.Completed,
                    VisualAuditAggregateOutcome.RepairRequired => VisualAuditStatus.RepairRequired,
                    VisualAuditAggregateOutcome.Blocked => VisualAuditStatus.Blocked,
                    VisualAuditAggregateOutcome.HumanReviewRequired => VisualAuditStatus.AwaitingHumanReview,
                    _ => throw new VisualAuditValidationException("A terminal aggregate outcome is required.")
                };
                return state with { Findings = completion.Findings.ToArray(), Outcome = completion.Outcome, Status = status,
                    Revision = state.Revision + 1, MessageId = MessageId(state.AuditId, state.Revision + 1), UpdatedAtUtc = at };
            }, (connection, tx, _) => PersistFindings(connection, tx, completion), ct);

    public ValueTask<VisualAuditState> DecideAsync(VisualAuditDecisionCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.AuditId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, Hash(JsonSerializer.Serialize(command)), "DECIDE", command.Actor, command.Rationale, at, state =>
            {
                RequireText(command.Authority, command.Scope, command.Rationale, command.Evidence, command.EvidenceDigest);
                if (state.Status != VisualAuditStatus.AwaitingHumanReview)
                    throw new VisualAuditTransitionException("Human decision requires an awaiting-review audit.");
                var decision = new VisualAuditDecision(command.RequestId, command.Decision, command.Authority, command.Scope,
                    command.Rationale, command.EvidenceDigest, at);
                var (outcome, status) = command.Decision switch
                {
                    VisualAuditHumanDecision.Approve => (VisualAuditAggregateOutcome.Pass, VisualAuditStatus.Completed),
                    VisualAuditHumanDecision.ReturnToRepair => (VisualAuditAggregateOutcome.RepairRequired, VisualAuditStatus.RepairRequired),
                    VisualAuditHumanDecision.Reject => (VisualAuditAggregateOutcome.Blocked, VisualAuditStatus.Blocked),
                    VisualAuditHumanDecision.Escalate => (VisualAuditAggregateOutcome.HumanReviewRequired, VisualAuditStatus.AwaitingHumanReview),
                    _ => throw new VisualAuditValidationException("Unsupported human decision.")
                };
                return state with { Decisions = state.Decisions.Append(decision).ToArray(), Outcome = outcome, Status = status,
                    Revision = state.Revision + 1, MessageId = MessageId(state.AuditId, state.Revision + 1), UpdatedAtUtc = at };
            }, (connection, tx, next) => PersistDecision(connection, tx, command, next.Decisions[^1], at), ct);

    public ValueTask<VisualAuditState> ApplyWaiverAsync(VisualAuditWaiverCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.AuditId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, Hash(JsonSerializer.Serialize(command)), "WAIVER", command.Actor, command.Rationale, at, state =>
            {
                RequireText(command.Authority, command.Scope, command.Rationale, command.Evidence, command.EvidenceDigest);
                if (command.FindingIds.Count == 0 || command.ExpiresAtUtc <= at)
                    throw new VisualAuditValidationException("A scoped, future-expiring waiver is required.");
                var selected = state.Findings.Where(x => command.FindingIds.Contains(x.FindingId)).ToArray();
                if (selected.Length != command.FindingIds.Count || selected.Any(x => !x.Waivable))
                    throw new VisualAuditValidationException("Waiver contains unknown or non-waivable findings.");
                var waiver = new VisualAuditWaiver(command.RequestId, command.FindingIds, command.Authority, command.Scope,
                    command.Rationale, command.EvidenceDigest, command.ExpiresAtUtc, at);
                return state with { Waivers = state.Waivers.Append(waiver).ToArray(), Revision = state.Revision + 1,
                    MessageId = MessageId(state.AuditId, state.Revision + 1), UpdatedAtUtc = at };
            }, (connection, tx, next) => PersistWaiver(connection, tx, command, next.Waivers[^1]), ct);

    public ValueTask<VisualAuditState?> GetAsync(string workspaceId, Guid auditId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Load(workspaceId, auditId));
    }

    private async ValueTask<VisualAuditState> Mutate(string workspaceId, Guid auditId, long expectedRevision,
        Guid operationId, string fingerprint, string payload, string operation, string actor, string reason, DateTimeOffset at,
        Func<VisualAuditState, VisualAuditState> mutation,
        Action<SqliteConnection, SqliteTransaction, VisualAuditState>? sideEffect, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var replay = LoadReceipt(workspaceId, operationId);
            if (replay is not null)
            {
                if (!StringComparer.Ordinal.Equals(replay.Value.Fingerprint, fingerprint) || !StringComparer.Ordinal.Equals(replay.Value.Payload, payload))
                    throw new VisualAuditConflictException("Operation reused with a different payload.");
                return replay.Value.State;
            }
            var state = Load(workspaceId, auditId) ?? throw new VisualAuditValidationException("Visual audit not found.");
            if (state.Revision != expectedRevision) throw new VisualAuditConflictException("Stale revision.");
            RequireAuthorities(state);
            var next = mutation(state);
            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            var affected = Exec(connection, tx, "UPDATE visual_audits SET outcome=$o,status=$s,revision=$r,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND audit_id=$id AND revision=$expected",
                ("$o",EnumText(next.Outcome)),("$s",EnumText(next.Status)),("$r",next.Revision),("$m",next.MessageId!.Value.ToString("D")),
                ("$at",at.ToString("O")),("$w",workspaceId),("$id",auditId.ToString("D")),("$expected",expectedRevision));
            if (affected != 1) throw new VisualAuditConflictException("Stale revision.");
            sideEffect?.Invoke(connection, tx, next);
            History(connection, tx, next, operation, actor, reason, at);
            Receipt(connection, tx, workspaceId, auditId, operationId, fingerprint, payload, next, at);
            Outbox(connection, tx, next, $"visual.audit.{next.Status.ToString().ToLowerInvariant()}", at);
            tx.Commit();
            return next;
        }
        finally { _gate.Release(); }
    }

    private VisualAuditState? Load(string workspaceId, Guid auditId)
    {
        using var connection = _factory.OpenConnection(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT snapshot_json FROM visual_audit_history WHERE workspace_id=$w AND audit_id=$id ORDER BY revision DESC,occurred_at_utc DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$w", workspaceId); cmd.Parameters.AddWithValue("$id", auditId.ToString("D"));
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<VisualAuditState>(json)
            ?? throw new VisualAuditConflictException("Invalid persisted visual audit state.");
    }

    private (string Fingerprint, string Payload, VisualAuditState State)? LoadReceipt(string workspaceId, Guid operationId)
    {
        using var connection = _factory.OpenConnection(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT request_fingerprint,payload_digest,response_json FROM visual_audit_receipts WHERE workspace_id=$w AND operation_id=$o";
        cmd.Parameters.AddWithValue("$w", workspaceId); cmd.Parameters.AddWithValue("$o", operationId.ToString("D"));
        using var reader = cmd.ExecuteReader(); if (!reader.Read()) return null;
        var state = JsonSerializer.Deserialize<VisualAuditState>(reader.GetString(2))
            ?? throw new VisualAuditConflictException("Invalid persisted visual audit receipt.");
        return (reader.GetString(0), reader.GetString(1), state);
    }

    private void RequireReceipt(string workspaceId, Guid auditId, Guid operationId, string fingerprint, string payload)
    {
        var receipt = LoadReceipt(workspaceId, operationId);
        if (receipt is null || receipt.Value.State.AuditId != auditId || !StringComparer.Ordinal.Equals(receipt.Value.Fingerprint, fingerprint)
            || !StringComparer.Ordinal.Equals(receipt.Value.Payload, payload))
            throw new VisualAuditConflictException("Request reused with a different payload.");
    }

    private void RequireAuthorities(VisualAuditRequest request) => RequireAuthorities(request.WorkspaceId, request.ProjectId,
        request.AssetId, request.ExpectedAssetRevision, request.ExpectedAssetDigest, request.VisualBriefId,
        request.ExpectedVisualBriefRevision, request.ExpectedVisualBriefDigest, request.AdapterRequestId, request.AdapterEvidenceDigest);

    private void RequireAuthorities(VisualAuditState state) => RequireAuthorities(state.WorkspaceId, state.ProjectId,
        state.AssetId, state.ExpectedAssetRevision, state.ExpectedAssetDigest, state.VisualBriefId,
        state.ExpectedVisualBriefRevision, state.ExpectedVisualBriefDigest, state.AdapterRequestId, state.AdapterEvidenceDigest);

    private void RequireAuthorities(string workspaceId, Guid projectId, Guid assetId, long assetRevision, string assetDigest,
        Guid briefId, long briefRevision, string briefDigest, Guid? adapterRequestId, string? adapterEvidenceDigest)
    {
        using var connection = _factory.OpenConnection();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT project_id,visual_brief_id,revision,content_digest,status FROM visual_assets WHERE workspace_id=$w AND asset_id=$id";
            cmd.Parameters.AddWithValue("$w", workspaceId); cmd.Parameters.AddWithValue("$id", assetId.ToString("D"));
            using var reader = cmd.ExecuteReader();
            if (!reader.Read() || Guid.Parse(reader.GetString(0)) != projectId || Guid.Parse(reader.GetString(1)) != briefId
                || reader.GetInt64(2) != assetRevision || !StringComparer.Ordinal.Equals(reader.GetString(3), assetDigest)
                || reader.GetString(4) is "STALE" or "REVOKED" or "QUARANTINED")
                throw new VisualAuditValidationException("Asset authority is not exact and auditable.");
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT project_id,revision,status FROM visual_briefs WHERE workspace_id=$w AND brief_id=$id";
            cmd.Parameters.AddWithValue("$w", workspaceId); cmd.Parameters.AddWithValue("$id", briefId.ToString("D"));
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) throw new VisualAuditValidationException("Visual brief authority not found.");
            var status = reader.GetString(2); var actual = Hash($"{workspaceId}:{briefId:D}:{reader.GetInt64(1)}:{status}");
            if (Guid.Parse(reader.GetString(0)) != projectId || reader.GetInt64(1) != briefRevision || status != "APPROVED"
                || !StringComparer.Ordinal.Equals(actual, briefDigest))
                throw new VisualAuditValidationException("Visual brief authority is not exact, current and approved.");
        }
        if (adapterRequestId is not null)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT status FROM image_adapter_requests WHERE workspace_id=$w AND request_id=$id";
            cmd.Parameters.AddWithValue("$w", workspaceId); cmd.Parameters.AddWithValue("$id", adapterRequestId.Value.ToString("D"));
            var status = cmd.ExecuteScalar() as string;
            if (status != "COMPLETED" || string.IsNullOrWhiteSpace(adapterEvidenceDigest))
                throw new VisualAuditValidationException("VS-102 adapter provenance linkage is incomplete.");
        }
    }

    private static void PersistChecks(SqliteConnection c, SqliteTransaction tx, VisualAuditCheckBatch batch)
    {
        foreach (var check in batch.Checks)
        {
            RequireText(check.PolicyId, check.PolicyVersion, check.Evidence, check.EvidenceDigest, check.ProviderId, check.ProviderVersion);
            Exec(c, tx, "INSERT INTO visual_audit_checks(workspace_id,audit_id,check_id,execution_id,check_kind,outcome,severity,confidence,policy_id,policy_version,evidence,evidence_digest,finding_code,repair_recommendation,provider_id,provider_version,completed_at_utc) VALUES($w,$a,$id,$e,$k,$o,$s,$c,$pi,$pv,$ev,$ed,$f,$r,$pr,$vr,$at)",
                ("$w",batch.WorkspaceId),("$a",batch.AuditId.ToString("D")),("$id",check.CheckId.ToString("D")),("$e",batch.ExecutionId.ToString("D")),
                ("$k",EnumText(check.Kind)),("$o",EnumText(check.Outcome)),("$s",EnumText(check.Severity)),("$c",check.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("$pi",check.PolicyId),("$pv",check.PolicyVersion),("$ev",check.Evidence),("$ed",check.EvidenceDigest),
                ("$f",check.FindingCode is null ? DBNull.Value : EnumText(check.FindingCode.Value)),("$r",check.RepairRecommendation is null ? DBNull.Value : check.RepairRecommendation),
                ("$pr",check.ProviderId),("$vr",check.ProviderVersion),("$at",check.CompletedAtUtc.ToString("O")));
        }
    }

    private static void PersistFindings(SqliteConnection c, SqliteTransaction tx, VisualAuditCompletion completion)
    {
        foreach (var finding in completion.Findings)
            Exec(c, tx, "INSERT INTO visual_audit_findings(workspace_id,audit_id,finding_id,check_id,finding_code,severity,summary,evidence,evidence_digest,waivable,repair_recommendation) VALUES($w,$a,$id,$c,$f,$s,$m,$e,$d,$v,$r)",
                ("$w",completion.WorkspaceId),("$a",completion.AuditId.ToString("D")),("$id",finding.FindingId.ToString("D")),("$c",finding.CheckId.ToString("D")),
                ("$f",EnumText(finding.Code)),("$s",EnumText(finding.Severity)),("$m",finding.Summary),("$e",finding.Evidence),("$d",finding.EvidenceDigest),
                ("$v",finding.Waivable ? 1 : 0),("$r",finding.RepairRecommendation is null ? DBNull.Value : finding.RepairRecommendation));
    }

    private static void PersistDecision(SqliteConnection c, SqliteTransaction tx, VisualAuditDecisionCommand command, VisualAuditDecision decision, DateTimeOffset at) =>
        Exec(c, tx, "INSERT INTO visual_audit_decisions VALUES($w,$a,$id,$d,$au,$s,$r,$e,$at)",
            ("$w",command.WorkspaceId),("$a",command.AuditId.ToString("D")),("$id",decision.DecisionId.ToString("D")),("$d",EnumText(decision.Decision)),
            ("$au",decision.Authority),("$s",decision.Scope),("$r",decision.Rationale),("$e",decision.EvidenceDigest),("$at",at.ToString("O")));

    private static void PersistWaiver(SqliteConnection c, SqliteTransaction tx, VisualAuditWaiverCommand command, VisualAuditWaiver waiver) =>
        Exec(c, tx, "INSERT INTO visual_audit_waivers VALUES($w,$a,$id,$f,$au,$s,$r,$e,$x,$at)",
            ("$w",command.WorkspaceId),("$a",command.AuditId.ToString("D")),("$id",waiver.WaiverId.ToString("D")),
            ("$f",JsonSerializer.Serialize(waiver.FindingIds)),("$au",waiver.Authority),("$s",waiver.Scope),("$r",waiver.Rationale),
            ("$e",waiver.EvidenceDigest),("$x",waiver.ExpiresAtUtc.ToString("O")),("$at",waiver.CreatedAtUtc.ToString("O")));

    private static void History(SqliteConnection c, SqliteTransaction tx, VisualAuditState state, string type, string actor, string reason, DateTimeOffset at) =>
        Exec(c, tx, "INSERT INTO visual_audit_history(workspace_id,history_id,audit_id,revision,event_type,actor,reason,snapshot_json,occurred_at_utc) VALUES($w,$h,$a,$r,$e,$actor,$reason,$s,$at)",
            ("$w",state.WorkspaceId),("$h",DeterministicGuid(state.AuditId,$"history:{state.Revision}:{type}").ToString("D")),
            ("$a",state.AuditId.ToString("D")),("$r",state.Revision),("$e",type),("$actor",actor),("$reason",reason),
            ("$s",JsonSerializer.Serialize(state)),("$at",at.ToString("O")));

    private static void Receipt(SqliteConnection c, SqliteTransaction tx, string workspaceId, Guid auditId, Guid operationId,
        string fingerprint, string payload, VisualAuditState state, DateTimeOffset at) =>
        Exec(c, tx, "INSERT INTO visual_audit_receipts(workspace_id,operation_id,audit_id,request_fingerprint,payload_digest,response_json,created_at_utc) VALUES($w,$o,$a,$f,$p,$r,$at)",
            ("$w",workspaceId),("$o",operationId.ToString("D")),("$a",auditId.ToString("D")),("$f",fingerprint),("$p",payload),
            ("$r",JsonSerializer.Serialize(state)),("$at",at.ToString("O")));

    private static void Outbox(SqliteConnection c, SqliteTransaction tx, VisualAuditState state, string type, DateTimeOffset at) =>
        Exec(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,created_at_utc) VALUES($id,$t,'1',$p,$at,$at,'PENDING',0,$at)",
            ("$id",state.MessageId!.Value.ToString("D")),("$t",type),("$p",JsonSerializer.Serialize(new { state.WorkspaceId, state.AuditId, state.AssetId, state.Revision, state.Status, state.Outcome })),("$at",at.ToString("O")));

    private static void ValidateRequest(VisualAuditRequest request)
    {
        if (request.AuditId == Guid.Empty || request.ProjectId == Guid.Empty || request.AssetId == Guid.Empty || request.VisualBriefId == Guid.Empty
            || request.ExpectedAssetRevision < 1 || request.ExpectedVisualBriefRevision < 1 || request.RequestedChecks.Count == 0)
            throw new VisualAuditValidationException("Complete audit identity, authority and check coverage are required.");
        RequireText(request.WorkspaceId, request.ExpectedAssetDigest, request.ExpectedVisualBriefDigest, request.PolicyId,
            request.PolicyVersion, request.Actor, request.RequestFingerprint);
    }

    private static int Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object)[] values)
    { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var value in values) cmd.Parameters.AddWithValue(value.Item1, value.Item2); return cmd.ExecuteNonQuery(); }
    private static string EnumText<T>(T value) where T : struct, Enum => value.ToString().ToUpperInvariant();
    private static void RequireText(params string[] values) { if (values.Any(string.IsNullOrWhiteSpace)) throw new VisualAuditValidationException("Required visual audit evidence is missing."); }
    private static Guid MessageId(Guid id, long revision) => DeterministicGuid(id, $"message:{revision}");
    private static Guid DeterministicGuid(Guid scope, string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{scope:D}:{value}"))[..16]);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public ValueTask DisposeAsync() { _gate.Dispose(); return ValueTask.CompletedTask; }
}

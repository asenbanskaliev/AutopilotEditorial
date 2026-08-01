using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Publishing;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Publishing;

public sealed class SqliteProofWorkflowStore : IProofWorkflowStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteProofWorkflowStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async ValueTask<ProofSubmissionResult> SubmitAsync(ProofRequest request, DateTimeOffset at, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var payload = Digest(JsonSerializer.Serialize(request));
            var replay = LoadReceipt(request.WorkspaceId, request.RequestId);
            if (replay is not null)
            {
                RequireReplay(replay.Value, request.ProofId, request.RequestFingerprint, payload);
                return new ProofSubmissionResult(replay.Value.State, true);
            }
            if (Load(request.WorkspaceId, request.ProofId) is not null)
                throw new ProofConflictException("Proof workflow already exists.");

            var state = new ProofState(request.ProofId, request.ProjectId, request.WorkspaceId, request.Authority,
                request.ProofType, request.Locale, request.Reviewer, request.SupersedesProofId,
                Array.Empty<ProofChecklistExecution>(), Array.Empty<ProofFinding>(), null, null,
                ProofStatus.Draft, 1, MessageId(request.ProofId, 1), at, at);

            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            Exec(connection, tx,
                "INSERT INTO proof_workflows(workspace_id,proof_id,project_id,authority_json,proof_type,locale,reviewer,supersedes_proof_id,executions_json,findings_json,physical_receipt_json,evidence_digest,status,revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,$t,$l,$r,$sp,$e,$f,NULL,NULL,$s,1,$m,$at,$at)",
                ("$w", request.WorkspaceId), ("$id", request.ProofId.ToString("D")), ("$p", request.ProjectId.ToString("D")),
                ("$a", JsonSerializer.Serialize(request.Authority)), ("$t", request.ProofType.ToString()),
                ("$l", request.Locale), ("$r", request.Reviewer),
                ("$sp", request.SupersedesProofId?.ToString("D")), ("$e", "[]"), ("$f", "[]"),
                ("$s", state.Status.ToString()), ("$m", state.MessageId!.Value.ToString("D")), ("$at", at.ToString("O")));
            History(connection, tx, state, "SUBMIT", request.Actor, "Proof workflow submitted", at);
            Receipt(connection, tx, request.WorkspaceId, request.RequestId, request.ProofId, request.RequestFingerprint, payload, state, at);
            Outbox(connection, tx, state, "proof.submitted", at);
            tx.Commit();
            return new ProofSubmissionResult(state, false);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<ProofState> EvaluateAsync(ProofEvaluationCommand command,
        IReadOnlyList<ProofChecklistExecution> executions, IReadOnlyList<ProofFinding> findings,
        string evidenceDigest, DateTimeOffset at, CancellationToken ct = default)
    {
        if (executions is null || executions.Count == 0 || findings is null || string.IsNullOrWhiteSpace(evidenceDigest))
            throw new ProofValidationException("Proof evaluation evidence is invalid.");
        var orderedExecutions = executions.OrderBy(x => x.ChecklistId, StringComparer.Ordinal).ThenBy(x => x.Version, StringComparer.Ordinal).ToArray();
        var orderedFindings = findings.OrderBy(x => x.ChecklistId, StringComparer.Ordinal).ThenBy(x => x.RuleId, StringComparer.Ordinal).ThenBy(x => x.FindingId).ToArray();
        var payload = Digest(JsonSerializer.Serialize(new { Command = command, Executions = orderedExecutions, Findings = orderedFindings, EvidenceDigest = evidenceDigest }));
        return Mutate(command.WorkspaceId, command.ProofId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, payload, "EVALUATE", command.Actor, "Proof evaluated", at,
            state =>
            {
                if (state.Status is not ProofStatus.Draft and not ProofStatus.CorrectionRequired)
                    throw new ProofTransitionException("Only draft or correction-required proofs can be evaluated.");
                var status = state.ProofType == ProofType.Physical ? ProofStatus.AwaitingPhysicalReceipt : ProofStatus.Evaluated;
                return state with { Executions = orderedExecutions, Findings = orderedFindings, EvidenceDigest = evidenceDigest,
                    Status = status, Revision = state.Revision + 1, MessageId = MessageId(state.ProofId, state.Revision + 1), UpdatedAtUtc = at };
            }, (connection, tx, next) => PersistEvaluation(connection, tx, next), ct);
    }

    public ValueTask<ProofState> RecordPhysicalReceiptAsync(PhysicalProofReceiptCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.ProofId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, Digest(JsonSerializer.Serialize(command)), "PHYSICAL_RECEIPT", command.Actor,
            "Physical proof receipt recorded", at,
            state =>
            {
                if (state.ProofType != ProofType.Physical || state.Status != ProofStatus.AwaitingPhysicalReceipt)
                    throw new ProofTransitionException("Physical receipt requires an evaluated physical proof.");
                var receipt = new PhysicalProofReceipt(command.Provider, command.OrderReference, command.ReceivedDate,
                    command.InspectedArtifactDigest, command.ReviewerAttestation, at);
                return state with { PhysicalReceipt = receipt, Status = ProofStatus.Evaluated,
                    Revision = state.Revision + 1, MessageId = MessageId(state.ProofId, state.Revision + 1), UpdatedAtUtc = at };
            }, (connection, tx, next) => PersistPhysicalReceipt(connection, tx, command, next, at), ct);

    public ValueTask<ProofState> DecideAsync(ProofDecisionCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.ProofId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, Digest(JsonSerializer.Serialize(command)), "DECIDE", command.Actor, command.Reason, at,
            state => state with
            {
                Status = command.Decision switch
                {
                    ProofDecision.Approve => ProofStatus.Approved,
                    ProofDecision.ReturnToCorrection => ProofStatus.CorrectionRequired,
                    ProofDecision.Reject => ProofStatus.Rejected,
                    ProofDecision.Supersede => ProofStatus.Superseded,
                    _ => throw new ProofValidationException("Unsupported proof decision.")
                },
                Revision = state.Revision + 1,
                MessageId = MessageId(state.ProofId, state.Revision + 1),
                UpdatedAtUtc = at
            }, (connection, tx, next) => PersistDecision(connection, tx, command, next, at), ct);

    public ValueTask<ProofState?> GetAsync(string workspaceId, Guid proofId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Load(workspaceId, proofId));
    }

    private async ValueTask<ProofState> Mutate(string workspaceId, Guid proofId, long expectedRevision,
        Guid operationId, string fingerprint, string payload, string operation, string actor, string reason,
        DateTimeOffset at, Func<ProofState, ProofState> mutation,
        Action<SqliteConnection, SqliteTransaction, ProofState> sideEffect, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var replay = LoadReceipt(workspaceId, operationId);
            if (replay is not null)
            {
                RequireReplay(replay.Value, proofId, fingerprint, payload);
                return replay.Value.State;
            }
            var current = Load(workspaceId, proofId) ?? throw new ProofValidationException("Proof workflow not found.");
            if (current.Revision != expectedRevision) throw new ProofConflictException("Stale proof revision.");
            var next = mutation(current);
            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            var affected = Exec(connection, tx,
                "UPDATE proof_workflows SET executions_json=$e,findings_json=$f,physical_receipt_json=$pr,evidence_digest=$ed,status=$s,revision=$v,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND proof_id=$id AND revision=$expected",
                ("$e", JsonSerializer.Serialize(next.Executions)), ("$f", JsonSerializer.Serialize(next.Findings)),
                ("$pr", next.PhysicalReceipt is null ? null : JsonSerializer.Serialize(next.PhysicalReceipt)),
                ("$ed", next.EvidenceDigest), ("$s", next.Status.ToString()), ("$v", next.Revision),
                ("$m", next.MessageId!.Value.ToString("D")), ("$at", at.ToString("O")),
                ("$w", workspaceId), ("$id", proofId.ToString("D")), ("$expected", expectedRevision));
            if (affected != 1) throw new ProofConflictException("Stale proof revision.");
            sideEffect(connection, tx, next);
            History(connection, tx, next, operation, actor, reason, at);
            Receipt(connection, tx, workspaceId, operationId, proofId, fingerprint, payload, next, at);
            Outbox(connection, tx, next, $"proof.{next.Status.ToString().ToLowerInvariant()}", at);
            tx.Commit();
            return next;
        }
        finally { _gate.Release(); }
    }

    private ProofState? Load(string workspaceId, Guid proofId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM proof_history WHERE workspace_id=$w AND proof_id=$id ORDER BY revision DESC LIMIT 1";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$id", proofId.ToString("D"));
        var json = command.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<ProofState>(json)
            ?? throw new ProofConflictException("Invalid persisted proof state.");
    }

    private (Guid ProofId, string Fingerprint, string Payload, ProofState State)? LoadReceipt(string workspaceId, Guid operationId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT proof_id,request_fingerprint,payload_digest,response_json FROM proof_receipts WHERE workspace_id=$w AND operation_id=$o";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$o", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var state = JsonSerializer.Deserialize<ProofState>(reader.GetString(3))
            ?? throw new ProofConflictException("Invalid persisted proof receipt.");
        return (Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), state);
    }

    private static void RequireReplay((Guid ProofId, string Fingerprint, string Payload, ProofState State) replay,
        Guid proofId, string fingerprint, string payload)
    {
        if (replay.ProofId != proofId || !StringComparer.Ordinal.Equals(replay.Fingerprint, fingerprint)
            || !StringComparer.Ordinal.Equals(replay.Payload, payload))
            throw new ProofConflictException("Operation reused with a different payload.");
    }

    private static void PersistEvaluation(SqliteConnection connection, SqliteTransaction tx, ProofState state)
    {
        Exec(connection, tx, "DELETE FROM proof_checklist_executions WHERE workspace_id=$w AND proof_id=$p",
            ("$w", state.WorkspaceId), ("$p", state.ProofId.ToString("D")));
        Exec(connection, tx, "DELETE FROM proof_findings WHERE workspace_id=$w AND proof_id=$p",
            ("$w", state.WorkspaceId), ("$p", state.ProofId.ToString("D")));
        foreach (var execution in state.Executions)
            Exec(connection, tx, "INSERT INTO proof_checklist_executions VALUES($w,$p,$v,$id,$cv,$i,$o,$at,$j)",
                ("$w", state.WorkspaceId), ("$p", state.ProofId.ToString("D")), ("$v", state.Revision),
                ("$id", execution.ChecklistId), ("$cv", execution.Version), ("$i", execution.InputDigest),
                ("$o", execution.OutputDigest), ("$at", execution.ExecutedAtUtc.ToString("O")), ("$j", JsonSerializer.Serialize(execution)));
        foreach (var finding in state.Findings)
            Exec(connection, tx, "INSERT INTO proof_findings VALUES($w,$p,$id,$c,$cv,$r,$s,$l,$a,$e,$st,$d,$j)",
                ("$w", state.WorkspaceId), ("$p", state.ProofId.ToString("D")), ("$id", finding.FindingId.ToString("D")),
                ("$c", finding.ChecklistId), ("$cv", finding.ChecklistVersion), ("$r", finding.RuleId),
                ("$s", finding.Severity.ToString()), ("$l", finding.Location), ("$a", finding.AnnotationDigest),
                ("$e", finding.EvidenceDigest), ("$st", finding.Status.ToString()), ("$d", finding.Disposition.ToString()),
                ("$j", JsonSerializer.Serialize(finding)));
    }

    private static void PersistPhysicalReceipt(SqliteConnection connection, SqliteTransaction tx,
        PhysicalProofReceiptCommand command, ProofState state, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO proof_physical_receipts VALUES($w,$p,$o,$pr,$or,$rd,$ad,$ra,$at,$j)",
        ("$w", command.WorkspaceId), ("$p", command.ProofId.ToString("D")), ("$o", command.RequestId.ToString("D")),
        ("$pr", command.Provider), ("$or", command.OrderReference), ("$rd", command.ReceivedDate.ToString("O")),
        ("$ad", command.InspectedArtifactDigest), ("$ra", command.ReviewerAttestation), ("$at", at.ToString("O")),
        ("$j", JsonSerializer.Serialize(state.PhysicalReceipt)));

    private static void PersistDecision(SqliteConnection connection, SqliteTransaction tx, ProofDecisionCommand command,
        ProofState state, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO proof_decisions VALUES($w,$p,$o,$d,$r,$e,$ed,$a,$v,$at)",
        ("$w", command.WorkspaceId), ("$p", command.ProofId.ToString("D")), ("$o", command.RequestId.ToString("D")),
        ("$d", command.Decision.ToString()), ("$r", command.Reason), ("$e", command.Evidence),
        ("$ed", command.EvidenceDigest), ("$a", command.Actor), ("$v", state.Revision), ("$at", at.ToString("O")));

    private static void History(SqliteConnection connection, SqliteTransaction tx, ProofState state,
        string operation, string actor, string reason, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO proof_history VALUES($w,$p,$v,$o,$a,$r,$j,$at)",
        ("$w", state.WorkspaceId), ("$p", state.ProofId.ToString("D")), ("$v", state.Revision),
        ("$o", operation), ("$a", actor), ("$r", reason), ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Receipt(SqliteConnection connection, SqliteTransaction tx, string workspaceId, Guid operationId,
        Guid proofId, string fingerprint, string payload, ProofState state, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO proof_receipts VALUES($w,$o,$p,$f,$d,$j,$at)",
        ("$w", workspaceId), ("$o", operationId.ToString("D")), ("$p", proofId.ToString("D")),
        ("$f", fingerprint), ("$d", payload), ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Outbox(SqliteConnection connection, SqliteTransaction tx, ProofState state,
        string eventType, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO proof_outbox VALUES($m,$w,$p,$v,$e,$j,$at)",
        ("$m", state.MessageId!.Value.ToString("D")), ("$w", state.WorkspaceId), ("$p", state.ProofId.ToString("D")),
        ("$v", state.Revision), ("$e", eventType), ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static int Exec(SqliteConnection connection, SqliteTransaction tx, string sql, params (string Name, object? Value)[] values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private static Guid MessageId(Guid proofId, long revision)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"proof|{proofId:D}|{revision}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

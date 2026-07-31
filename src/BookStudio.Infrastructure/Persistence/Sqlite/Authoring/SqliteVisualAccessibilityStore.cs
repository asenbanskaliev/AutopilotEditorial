using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteVisualAccessibilityStore : IVisualAccessibilityStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteVisualAccessibilityStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async ValueTask<VisualAccessibilitySubmissionResult> SubmitAsync(
        VisualAccessibilityCaseDraft draft, DateTimeOffset at, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var payload = Digest(JsonSerializer.Serialize(draft));
            var replay = LoadReceipt(draft.WorkspaceId, draft.RequestId);
            if (replay is not null)
            {
                RequireReplay(replay.Value, draft.AccessibilityCaseId, draft.RequestFingerprint, payload);
                return new VisualAccessibilitySubmissionResult(replay.Value.State, true);
            }

            if (Load(draft.WorkspaceId, draft.AccessibilityCaseId) is not null)
                throw new VisualAccessibilityConflictException("Accessibility case already exists.");

            var state = new VisualAccessibilityState(
                draft.AccessibilityCaseId, draft.ProjectId, draft.WorkspaceId, draft.Authority,
                draft.Channel, draft.Locale, draft.Visuals, [], [], VisualAccessibilityStatus.Draft,
                1, MessageId(draft.AccessibilityCaseId, 1), at, at);

            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            Exec(connection, tx,
                "INSERT INTO visual_accessibility_cases(workspace_id,accessibility_case_id,project_id,authority_json,channel,locale,visuals_json,status,revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,$c,$l,$v,$s,1,$m,$at,$at)",
                ("$w", draft.WorkspaceId), ("$id", draft.AccessibilityCaseId.ToString("D")),
                ("$p", draft.ProjectId.ToString("D")), ("$a", JsonSerializer.Serialize(draft.Authority)),
                ("$c", EnumText(draft.Channel)), ("$l", draft.Locale),
                ("$v", JsonSerializer.Serialize(draft.Visuals)), ("$s", EnumText(state.Status)),
                ("$m", state.MessageId!.Value.ToString("D")), ("$at", at.ToString("O")));
            History(connection, tx, state, "SUBMIT", draft.Actor, "visual accessibility case submitted", at);
            Receipt(connection, tx, draft.WorkspaceId, draft.RequestId, draft.AccessibilityCaseId,
                draft.RequestFingerprint, payload, state, at);
            Outbox(connection, tx, state, "visual.accessibility.submitted", at);
            tx.Commit();
            return new VisualAccessibilitySubmissionResult(state, false);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<VisualAccessibilityState> RecordAssessmentAsync(
        VisualAccessibilityAssessmentCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.AccessibilityCaseId, command.ExpectedRevision,
            command.RequestId, command.RequestFingerprint, Digest(JsonSerializer.Serialize(command)),
            "ASSESS", command.Actor, "visual accessibility assessed", at,
            state =>
            {
                var assessments = state.Assessments.Concat(command.Assessments)
                    .GroupBy(x => x.AssessmentId).Select(x => x.Last()).ToArray();
                var findings = assessments
                    .Where(x => x.Outcome is VisualAccessibilityOutcome.Fail or VisualAccessibilityOutcome.ReviewRequired)
                    .Select(x => new VisualAccessibilityFinding(
                        DeterministicGuid($"{state.AccessibilityCaseId:D}:{x.AssessmentId:D}:{x.FindingCode}"),
                        x.VisualUseId, x.FindingCode ?? "ACCESSIBILITY_REVIEW",
                        x.Outcome == VisualAccessibilityOutcome.Fail ? VisualAccessibilitySeverity.Blocking : VisualAccessibilitySeverity.Major,
                        x.RepairRecommendation ?? "Accessibility evidence requires review.",
                        x.Evidence, x.EvidenceDigest, x.RepairRecommendation))
                    .ToArray();
                var status = findings.Any(x => x.Severity == VisualAccessibilitySeverity.Blocking)
                    ? VisualAccessibilityStatus.ReviewRequired
                    : VisualAccessibilityStatus.Assessed;
                return state with
                {
                    Assessments = assessments,
                    Findings = findings,
                    Status = status,
                    Revision = state.Revision + 1,
                    MessageId = MessageId(state.AccessibilityCaseId, state.Revision + 1),
                    UpdatedAtUtc = at
                };
            },
            (connection, tx, next) => PersistAssessments(connection, tx, command, next, at), ct);

    public ValueTask<VisualAccessibilityState> DecideAsync(
        VisualAccessibilityDecisionCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.AccessibilityCaseId, command.ExpectedRevision,
            command.RequestId, command.RequestFingerprint, Digest(JsonSerializer.Serialize(command)),
            "DECIDE", command.Actor, command.Reason, at,
            state => state with
            {
                Status = command.Decision switch
                {
                    VisualAccessibilityDecision.Approve => VisualAccessibilityStatus.Approved,
                    VisualAccessibilityDecision.ReturnToRepair => VisualAccessibilityStatus.RepairRequired,
                    VisualAccessibilityDecision.Reject => VisualAccessibilityStatus.Rejected,
                    VisualAccessibilityDecision.Supersede => VisualAccessibilityStatus.Superseded,
                    _ => throw new VisualAccessibilityValidationException("Unsupported accessibility decision.")
                },
                Revision = state.Revision + 1,
                MessageId = MessageId(state.AccessibilityCaseId, state.Revision + 1),
                UpdatedAtUtc = at
            },
            (connection, tx, next) => PersistDecision(connection, tx, command, next, at), ct);

    public ValueTask<VisualAccessibilityState?> GetAsync(
        string workspaceId, Guid accessibilityCaseId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Load(workspaceId, accessibilityCaseId));
    }

    private async ValueTask<VisualAccessibilityState> Mutate(
        string workspaceId, Guid caseId, long expectedRevision, Guid operationId,
        string fingerprint, string payload, string operation, string actor, string reason,
        DateTimeOffset at, Func<VisualAccessibilityState, VisualAccessibilityState> mutation,
        Action<SqliteConnection, SqliteTransaction, VisualAccessibilityState> sideEffect,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var replay = LoadReceipt(workspaceId, operationId);
            if (replay is not null)
            {
                RequireReplay(replay.Value, caseId, fingerprint, payload);
                return replay.Value.State;
            }

            var current = Load(workspaceId, caseId)
                ?? throw new VisualAccessibilityValidationException("Visual accessibility case not found.");
            if (current.Revision != expectedRevision)
                throw new VisualAccessibilityConflictException("Stale visual accessibility revision.");
            var next = mutation(current);

            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            var affected = Exec(connection, tx,
                "UPDATE visual_accessibility_cases SET status=$s,revision=$r,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND accessibility_case_id=$id AND revision=$expected",
                ("$s", EnumText(next.Status)), ("$r", next.Revision),
                ("$m", next.MessageId!.Value.ToString("D")), ("$at", at.ToString("O")),
                ("$w", workspaceId), ("$id", caseId.ToString("D")), ("$expected", expectedRevision));
            if (affected != 1) throw new VisualAccessibilityConflictException("Stale visual accessibility revision.");
            sideEffect(connection, tx, next);
            History(connection, tx, next, operation, actor, reason, at);
            Receipt(connection, tx, workspaceId, operationId, caseId, fingerprint, payload, next, at);
            Outbox(connection, tx, next, $"visual.accessibility.{next.Status.ToString().ToLowerInvariant()}", at);
            tx.Commit();
            return next;
        }
        finally { _gate.Release(); }
    }

    private VisualAccessibilityState? Load(string workspaceId, Guid caseId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM visual_accessibility_history WHERE workspace_id=$w AND accessibility_case_id=$id ORDER BY revision DESC LIMIT 1";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$id", caseId.ToString("D"));
        var json = command.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<VisualAccessibilityState>(json)
            ?? throw new VisualAccessibilityConflictException("Invalid persisted accessibility state.");
    }

    private (Guid CaseId, string Fingerprint, string Payload, VisualAccessibilityState State)? LoadReceipt(string workspaceId, Guid operationId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT accessibility_case_id,request_fingerprint,payload_digest,response_json FROM visual_accessibility_receipts WHERE workspace_id=$w AND operation_id=$o";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$o", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var state = JsonSerializer.Deserialize<VisualAccessibilityState>(reader.GetString(3))
            ?? throw new VisualAccessibilityConflictException("Invalid persisted accessibility receipt.");
        return (Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), state);
    }

    private static void RequireReplay(
        (Guid CaseId, string Fingerprint, string Payload, VisualAccessibilityState State) replay,
        Guid caseId, string fingerprint, string payload)
    {
        if (replay.CaseId != caseId || !StringComparer.Ordinal.Equals(replay.Fingerprint, fingerprint) ||
            !StringComparer.Ordinal.Equals(replay.Payload, payload))
            throw new VisualAccessibilityConflictException("Operation reused with a different payload.");
    }

    private static void PersistAssessments(SqliteConnection connection, SqliteTransaction tx,
        VisualAccessibilityAssessmentCommand command, VisualAccessibilityState next, DateTimeOffset at)
    {
        foreach (var assessment in command.Assessments)
            Exec(connection, tx,
                "INSERT INTO visual_accessibility_assessments(workspace_id,accessibility_case_id,assessment_id,visual_use_id,assessment_kind,outcome,assessment_json,evidence_digest,created_at_utc) VALUES($w,$c,$id,$v,$k,$o,$j,$d,$at)",
                ("$w", command.WorkspaceId), ("$c", command.AccessibilityCaseId.ToString("D")),
                ("$id", assessment.AssessmentId.ToString("D")), ("$v", assessment.VisualUseId.ToString("D")),
                ("$k", EnumText(assessment.Kind)), ("$o", EnumText(assessment.Outcome)),
                ("$j", JsonSerializer.Serialize(assessment)), ("$d", assessment.EvidenceDigest), ("$at", at.ToString("O")));
        foreach (var finding in next.Findings)
            Exec(connection, tx,
                "INSERT OR REPLACE INTO visual_accessibility_findings(workspace_id,accessibility_case_id,finding_id,visual_use_id,code,severity,finding_json,evidence_digest,created_at_utc) VALUES($w,$c,$id,$v,$code,$s,$j,$d,$at)",
                ("$w", command.WorkspaceId), ("$c", command.AccessibilityCaseId.ToString("D")),
                ("$id", finding.FindingId.ToString("D")), ("$v", finding.VisualUseId.ToString("D")),
                ("$code", finding.Code), ("$s", EnumText(finding.Severity)),
                ("$j", JsonSerializer.Serialize(finding)), ("$d", finding.EvidenceDigest), ("$at", at.ToString("O")));
    }

    private static void PersistDecision(SqliteConnection connection, SqliteTransaction tx,
        VisualAccessibilityDecisionCommand command, VisualAccessibilityState next, DateTimeOffset at) =>
        Exec(connection, tx,
            "INSERT INTO visual_accessibility_decisions(workspace_id,accessibility_case_id,operation_id,decision,reason,evidence,evidence_digest,actor,revision,occurred_at_utc) VALUES($w,$c,$o,$d,$r,$e,$ed,$a,$v,$at)",
            ("$w", command.WorkspaceId), ("$c", command.AccessibilityCaseId.ToString("D")),
            ("$o", command.RequestId.ToString("D")), ("$d", EnumText(command.Decision)),
            ("$r", command.Reason), ("$e", command.Evidence), ("$ed", command.EvidenceDigest),
            ("$a", command.Actor), ("$v", next.Revision), ("$at", at.ToString("O")));

    private static void History(SqliteConnection connection, SqliteTransaction tx,
        VisualAccessibilityState state, string operation, string actor, string reason, DateTimeOffset at) =>
        Exec(connection, tx,
            "INSERT INTO visual_accessibility_history(workspace_id,accessibility_case_id,revision,operation,actor,reason,snapshot_json,occurred_at_utc) VALUES($w,$id,$r,$o,$a,$reason,$j,$at)",
            ("$w", state.WorkspaceId), ("$id", state.AccessibilityCaseId.ToString("D")),
            ("$r", state.Revision), ("$o", operation), ("$a", actor), ("$reason", reason),
            ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Receipt(SqliteConnection connection, SqliteTransaction tx, string workspaceId,
        Guid operationId, Guid caseId, string fingerprint, string payload, VisualAccessibilityState state, DateTimeOffset at) =>
        Exec(connection, tx,
            "INSERT INTO visual_accessibility_receipts(workspace_id,operation_id,accessibility_case_id,request_fingerprint,payload_digest,response_json,created_at_utc) VALUES($w,$o,$c,$f,$p,$j,$at)",
            ("$w", workspaceId), ("$o", operationId.ToString("D")), ("$c", caseId.ToString("D")),
            ("$f", fingerprint), ("$p", payload), ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Outbox(SqliteConnection connection, SqliteTransaction tx,
        VisualAccessibilityState state, string eventType, DateTimeOffset at) =>
        Exec(connection, tx,
            "INSERT INTO visual_accessibility_outbox(message_id,workspace_id,accessibility_case_id,revision,event_type,payload_json,created_at_utc) VALUES($m,$w,$c,$r,$e,$p,$at)",
            ("$m", state.MessageId!.Value.ToString("D")), ("$w", state.WorkspaceId),
            ("$c", state.AccessibilityCaseId.ToString("D")), ("$r", state.Revision),
            ("$e", eventType), ("$p", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static int Exec(SqliteConnection connection, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private static string EnumText<T>(T value) where T : struct, Enum =>
        value.ToString().ToUpperInvariant();

    private static Guid MessageId(Guid caseId, long revision) =>
        DeterministicGuid($"visual-accessibility:{caseId:D}:{revision}");

    private static Guid DeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Production;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Production;

public sealed class SqliteTechnicalPreflightStore : ITechnicalPreflightStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteTechnicalPreflightStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async ValueTask<TechnicalPreflightSubmissionResult> SubmitAsync(TechnicalPreflightRequest request, DateTimeOffset at, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var payload = Digest(JsonSerializer.Serialize(request));
            var replay = LoadReceipt(request.WorkspaceId, request.RequestId);
            if (replay is not null)
            {
                RequireReplay(replay.Value, request.RunId, request.RequestFingerprint, payload);
                return new TechnicalPreflightSubmissionResult(replay.Value.State, true);
            }
            if (Load(request.WorkspaceId, request.RunId) is not null)
                throw new TechnicalPreflightConflictException("Technical preflight run already exists.");

            var state = new TechnicalPreflightState(request.RunId, request.ProjectId, request.WorkspaceId,
                request.Authority, request.ProductionArtifactDigest, request.TargetProfile, request.Locale,
                request.RuleProfile, Array.Empty<TechnicalPreflightCheckResult>(), Array.Empty<TechnicalPreflightFinding>(),
                Array.Empty<TechnicalPreflightWaiver>(), null, TechnicalPreflightStatus.Draft, 1,
                MessageId(request.RunId, 1), at, at);

            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            Exec(connection, tx,
                "INSERT INTO technical_preflight_runs(workspace_id,run_id,project_id,authority_json,production_artifact_digest,target_profile,locale,rule_profile,executions_json,findings_json,waivers_json,evidence_digest,status,revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,$d,$t,$l,$rp,$x,$f,$wv,NULL,$s,1,$m,$at,$at)",
                ("$w", request.WorkspaceId), ("$id", request.RunId.ToString("D")), ("$p", request.ProjectId.ToString("D")),
                ("$a", JsonSerializer.Serialize(request.Authority)), ("$d", request.ProductionArtifactDigest),
                ("$t", request.TargetProfile), ("$l", request.Locale), ("$rp", request.RuleProfile),
                ("$x", "[]"), ("$f", "[]"), ("$wv", "[]"), ("$s", EnumText(state.Status)),
                ("$m", state.MessageId!.Value.ToString("D")), ("$at", at.ToString("O")));
            History(connection, tx, state, "SUBMIT", request.Actor, "Technical preflight submitted", at);
            Receipt(connection, tx, request.WorkspaceId, request.RequestId, request.RunId, request.RequestFingerprint, payload, state, at);
            Outbox(connection, tx, state, "technical-preflight.submitted", at);
            tx.Commit();
            return new TechnicalPreflightSubmissionResult(state, false);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<TechnicalPreflightState> EvaluateAsync(TechnicalPreflightEvaluationCommand command,
        IReadOnlyList<TechnicalPreflightCheckResult> executions, string evidenceDigest, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateExecutions(executions, evidenceDigest);
        var ordered = executions.OrderBy(x => x.CheckerId, StringComparer.Ordinal)
            .ThenBy(x => x.CheckerVersion, StringComparer.Ordinal).ToArray();
        var findings = ordered.SelectMany(x => x.Findings).OrderBy(x => x.Code, StringComparer.Ordinal)
            .ThenBy(x => x.FindingId).ToArray();
        var payload = Digest(JsonSerializer.Serialize(new { Command = command, Executions = ordered, EvidenceDigest = evidenceDigest }));
        return Mutate(command.WorkspaceId, command.RunId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, payload, "EVALUATE", command.Actor, "Technical preflight evaluated", at,
            state =>
            {
                if (state.Status != TechnicalPreflightStatus.Draft && state.Status != TechnicalPreflightStatus.RepairRequired)
                    throw new TechnicalPreflightTransitionException("Only draft or repair-required preflight can be evaluated.");
                return state with { Executions = ordered, Findings = findings, EvidenceDigest = evidenceDigest,
                    Status = TechnicalPreflightStatus.Evaluated, Revision = state.Revision + 1,
                    MessageId = MessageId(state.RunId, state.Revision + 1), UpdatedAtUtc = at };
            }, static (_, _, _) => { }, ct);
    }

    public ValueTask<TechnicalPreflightState> DecideAsync(TechnicalPreflightDecisionCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.RunId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, Digest(JsonSerializer.Serialize(command)), "DECIDE", command.Actor,
            command.Reason, at, state => state with
            {
                Waivers = command.Waivers.OrderBy(x => x.FindingId).ThenBy(x => x.WaiverId).ToArray(),
                Status = command.Decision switch
                {
                    TechnicalPreflightDecision.Approve => TechnicalPreflightStatus.Approved,
                    TechnicalPreflightDecision.ReturnToRepair => TechnicalPreflightStatus.RepairRequired,
                    TechnicalPreflightDecision.Reject => TechnicalPreflightStatus.Rejected,
                    TechnicalPreflightDecision.Supersede => TechnicalPreflightStatus.Superseded,
                    _ => throw new TechnicalPreflightValidationException("Unsupported technical preflight decision.")
                },
                Revision = state.Revision + 1,
                MessageId = MessageId(state.RunId, state.Revision + 1),
                UpdatedAtUtc = at
            }, (connection, tx, next) => PersistDecision(connection, tx, command, next, at), ct);

    public ValueTask<TechnicalPreflightState?> GetAsync(string workspaceId, Guid runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Load(workspaceId, runId));
    }

    private async ValueTask<TechnicalPreflightState> Mutate(string workspaceId, Guid runId, long expectedRevision,
        Guid operationId, string fingerprint, string payload, string operation, string actor, string reason,
        DateTimeOffset at, Func<TechnicalPreflightState, TechnicalPreflightState> mutation,
        Action<SqliteConnection, SqliteTransaction, TechnicalPreflightState> sideEffect, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var replay = LoadReceipt(workspaceId, operationId);
            if (replay is not null)
            {
                RequireReplay(replay.Value, runId, fingerprint, payload);
                return replay.Value.State;
            }
            var current = Load(workspaceId, runId) ?? throw new TechnicalPreflightValidationException("Technical preflight run not found.");
            if (current.Revision != expectedRevision) throw new TechnicalPreflightConflictException("Stale technical preflight revision.");
            var next = mutation(current);
            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            var affected = Exec(connection, tx,
                "UPDATE technical_preflight_runs SET executions_json=$x,findings_json=$f,waivers_json=$wv,evidence_digest=$ed,status=$s,revision=$v,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND run_id=$id AND revision=$expected",
                ("$x", JsonSerializer.Serialize(next.Executions)), ("$f", JsonSerializer.Serialize(next.Findings)),
                ("$wv", JsonSerializer.Serialize(next.Waivers)), ("$ed", next.EvidenceDigest), ("$s", EnumText(next.Status)),
                ("$v", next.Revision), ("$m", next.MessageId!.Value.ToString("D")), ("$at", at.ToString("O")),
                ("$w", workspaceId), ("$id", runId.ToString("D")), ("$expected", expectedRevision));
            if (affected != 1) throw new TechnicalPreflightConflictException("Stale technical preflight revision.");
            sideEffect(connection, tx, next);
            ReplaceEvidence(connection, tx, next);
            History(connection, tx, next, operation, actor, reason, at);
            Receipt(connection, tx, workspaceId, operationId, runId, fingerprint, payload, next, at);
            Outbox(connection, tx, next, $"technical-preflight.{next.Status.ToString().ToLowerInvariant()}", at);
            tx.Commit();
            return next;
        }
        finally { _gate.Release(); }
    }

    private TechnicalPreflightState? Load(string workspaceId, Guid runId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM technical_preflight_history WHERE workspace_id=$w AND run_id=$id ORDER BY revision DESC LIMIT 1";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$id", runId.ToString("D"));
        var json = command.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<TechnicalPreflightState>(json)
            ?? throw new TechnicalPreflightConflictException("Invalid persisted technical preflight state.");
    }

    private (Guid RunId, string Fingerprint, string Payload, TechnicalPreflightState State)? LoadReceipt(string workspaceId, Guid operationId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id,request_fingerprint,payload_digest,response_json FROM technical_preflight_receipts WHERE workspace_id=$w AND operation_id=$o";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$o", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var state = JsonSerializer.Deserialize<TechnicalPreflightState>(reader.GetString(3))
            ?? throw new TechnicalPreflightConflictException("Invalid persisted technical preflight receipt.");
        return (Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), state);
    }

    private static void RequireReplay((Guid RunId, string Fingerprint, string Payload, TechnicalPreflightState State) replay,
        Guid runId, string fingerprint, string payload)
    {
        if (replay.RunId != runId || !StringComparer.Ordinal.Equals(replay.Fingerprint, fingerprint)
            || !StringComparer.Ordinal.Equals(replay.Payload, payload))
            throw new TechnicalPreflightConflictException("Operation reused with a different payload.");
    }

    private static void ValidateExecutions(IReadOnlyList<TechnicalPreflightCheckResult> executions, string evidenceDigest)
    {
        if (executions.Count == 0 || string.IsNullOrWhiteSpace(evidenceDigest) ||
            executions.Select(x => $"{x.CheckerId}@{x.CheckerVersion}").Distinct(StringComparer.Ordinal).Count() != executions.Count ||
            executions.Any(x => string.IsNullOrWhiteSpace(x.CheckerId) || string.IsNullOrWhiteSpace(x.CheckerVersion) ||
                                string.IsNullOrWhiteSpace(x.RuleProfile) || string.IsNullOrWhiteSpace(x.InputDigest) ||
                                string.IsNullOrWhiteSpace(x.OutputDigest)))
            throw new TechnicalPreflightValidationException("Technical preflight evidence is invalid.");
    }

    private static void ReplaceEvidence(SqliteConnection connection, SqliteTransaction tx, TechnicalPreflightState state)
    {
        foreach (var table in new[] { "technical_preflight_executions", "technical_preflight_findings", "technical_preflight_waivers" })
            Exec(connection, tx, $"DELETE FROM {table} WHERE workspace_id=$w AND run_id=$r", ("$w", state.WorkspaceId), ("$r", state.RunId.ToString("D")));
        PersistEvidence(connection, tx, state);
    }

    private static void PersistEvidence(SqliteConnection connection, SqliteTransaction tx, TechnicalPreflightState state)
    {
        foreach (var x in state.Executions)
            Exec(connection, tx, "INSERT INTO technical_preflight_executions VALUES($w,$r,$id,$v,$p,$i,$o,$json)",
                ("$w", state.WorkspaceId), ("$r", state.RunId.ToString("D")), ("$id", x.CheckerId), ("$v", x.CheckerVersion),
                ("$p", x.RuleProfile), ("$i", x.InputDigest), ("$o", x.OutputDigest), ("$json", JsonSerializer.Serialize(x)));
        foreach (var x in state.Findings)
            Exec(connection, tx, "INSERT INTO technical_preflight_findings VALUES($w,$r,$id,$code,$sev,$loc,$rule,$ed,$status,$json)",
                ("$w", state.WorkspaceId), ("$r", state.RunId.ToString("D")), ("$id", x.FindingId.ToString("D")),
                ("$code", x.Code), ("$sev", EnumText(x.Severity)), ("$loc", x.Location), ("$rule", x.RuleId),
                ("$ed", x.EvidenceDigest), ("$status", EnumText(x.RemediationStatus)), ("$json", JsonSerializer.Serialize(x)));
        foreach (var x in state.Waivers)
            Exec(connection, tx, "INSERT INTO technical_preflight_waivers VALUES($w,$r,$id,$f,$expires,$by,$ed,$json)",
                ("$w", state.WorkspaceId), ("$r", state.RunId.ToString("D")), ("$id", x.WaiverId.ToString("D")),
                ("$f", x.FindingId.ToString("D")), ("$expires", x.ExpiresAtUtc.ToString("O")), ("$by", x.ApprovedBy),
                ("$ed", x.EvidenceDigest), ("$json", JsonSerializer.Serialize(x)));
    }

    private static void PersistDecision(SqliteConnection connection, SqliteTransaction tx, TechnicalPreflightDecisionCommand command,
        TechnicalPreflightState state, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO technical_preflight_decisions VALUES($w,$r,$o,$d,$reason,$e,$ed,$actor,$v,$at)",
        ("$w", command.WorkspaceId), ("$r", command.RunId.ToString("D")), ("$o", command.RequestId.ToString("D")),
        ("$d", EnumText(command.Decision)), ("$reason", command.Reason), ("$e", command.Evidence),
        ("$ed", command.EvidenceDigest), ("$actor", command.Actor), ("$v", state.Revision), ("$at", at.ToString("O")));

    private static void History(SqliteConnection connection, SqliteTransaction tx, TechnicalPreflightState state,
        string operation, string actor, string reason, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO technical_preflight_history VALUES($w,$r,$v,$o,$actor,$reason,$json,$at)",
        ("$w", state.WorkspaceId), ("$r", state.RunId.ToString("D")), ("$v", state.Revision), ("$o", operation),
        ("$actor", actor), ("$reason", reason), ("$json", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Receipt(SqliteConnection connection, SqliteTransaction tx, string workspaceId, Guid operationId,
        Guid runId, string fingerprint, string payload, TechnicalPreflightState state, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO technical_preflight_receipts VALUES($w,$o,$r,$f,$p,$json,$at)",
        ("$w", workspaceId), ("$o", operationId.ToString("D")), ("$r", runId.ToString("D")),
        ("$f", fingerprint), ("$p", payload), ("$json", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Outbox(SqliteConnection connection, SqliteTransaction tx, TechnicalPreflightState state,
        string eventType, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO technical_preflight_outbox VALUES($m,$w,$r,$v,$e,$p,$at)",
        ("$m", state.MessageId!.Value.ToString("D")), ("$w", state.WorkspaceId), ("$r", state.RunId.ToString("D")),
        ("$v", state.Revision), ("$e", eventType), ("$p", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static int Exec(SqliteConnection connection, SqliteTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private static string EnumText<T>(T value) where T : struct, Enum => value.ToString().ToUpperInvariant();
    private static Guid MessageId(Guid runId, long revision) => DeterministicGuid($"technical-preflight:{runId:D}:{revision}");
    private static Guid DeterministicGuid(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        _gate.Release();
        _gate.Dispose();
    }
}

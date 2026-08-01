using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Production;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Production;

public sealed class SqliteAccessibilityStore : IAccessibilityStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteAccessibilityStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async ValueTask<AccessibilitySubmissionResult> SubmitAsync(AccessibilityRequest request, AccessibilityEvidence evidence, DateTimeOffset at, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ValidateEvidence(evidence);
            var payload = Digest(JsonSerializer.Serialize(new { Request = request, Evidence = evidence }));
            var replay = LoadReceipt(request.WorkspaceId, request.RequestId);
            if (replay is not null)
            {
                RequireReplay(replay.Value, request.RunId, request.RequestFingerprint, payload);
                return new AccessibilitySubmissionResult(replay.Value.State, true);
            }
            if (Load(request.WorkspaceId, request.RunId) is not null)
                throw new AccessibilityConflictException("Accessibility run already exists.");

            var status = evidence.ManualReviews.Any(x => !x.Completed)
                ? AccessibilityStatus.ReviewRequired : AccessibilityStatus.Analyzed;
            var state = new AccessibilityState(request.RunId, request.ProjectId, request.WorkspaceId,
                request.Authority, request.Locale, request.TargetProfiles.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                evidence, status, 1, MessageId(request.RunId, 1), at, at);

            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            Exec(connection, tx,
                "INSERT INTO accessibility_runs(workspace_id,run_id,project_id,authority_json,locale,target_profiles_json,evidence_json,status,revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,$l,$t,$e,$s,1,$m,$at,$at)",
                ("$w", request.WorkspaceId), ("$id", request.RunId.ToString("D")), ("$p", request.ProjectId.ToString("D")),
                ("$a", JsonSerializer.Serialize(request.Authority)), ("$l", request.Locale),
                ("$t", JsonSerializer.Serialize(state.TargetProfiles)), ("$e", JsonSerializer.Serialize(evidence)),
                ("$s", EnumText(state.Status)), ("$m", state.MessageId!.Value.ToString("D")), ("$at", at.ToString("O")));
            PersistEvidence(connection, tx, state);
            History(connection, tx, state, "SUBMIT", request.Actor, "Accessibility analysis submitted", at);
            Receipt(connection, tx, request.WorkspaceId, request.RequestId, request.RunId, request.RequestFingerprint, payload, state, at);
            Outbox(connection, tx, state, "accessibility.analyzed", at);
            tx.Commit();
            return new AccessibilitySubmissionResult(state, false);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<AccessibilityState> ReviewAsync(AccessibilityReviewCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.RunId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, Digest(JsonSerializer.Serialize(command)), "REVIEW", command.Actor,
            "Accessibility review recorded", at, state =>
            {
                var reviews = command.Reviews.OrderBy(x => x.Scope, StringComparer.Ordinal).ThenBy(x => x.ReviewId).ToArray();
                var waivers = command.Waivers.OrderBy(x => x.FindingId).ThenBy(x => x.WaiverId).ToArray();
                var evidence = state.Evidence with { ManualReviews = reviews, Waivers = waivers };
                var status = reviews.Any(x => !x.Completed || x.Disposition is AccessibilityManualReviewDisposition.Pending or AccessibilityManualReviewDisposition.Fail)
                    ? AccessibilityStatus.ReviewRequired : AccessibilityStatus.Analyzed;
                return state with { Evidence = evidence, Status = status, Revision = state.Revision + 1,
                    MessageId = MessageId(state.RunId, state.Revision + 1), UpdatedAtUtc = at };
            }, static (_, _, _) => { }, ct);

    public ValueTask<AccessibilityState> DecideAsync(AccessibilityDecisionCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.RunId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, Digest(JsonSerializer.Serialize(command)), "DECIDE", command.Actor,
            command.Reason, at, state => state with
            {
                Status = command.Decision switch
                {
                    AccessibilityDecision.Approve => AccessibilityStatus.Approved,
                    AccessibilityDecision.ReturnToRepair => AccessibilityStatus.RepairRequired,
                    AccessibilityDecision.Reject => AccessibilityStatus.Rejected,
                    AccessibilityDecision.Supersede => AccessibilityStatus.Superseded,
                    _ => throw new AccessibilityValidationException("Unsupported accessibility decision.")
                },
                Revision = state.Revision + 1,
                MessageId = MessageId(state.RunId, state.Revision + 1),
                UpdatedAtUtc = at
            }, (connection, tx, next) => PersistDecision(connection, tx, command, next, at), ct);

    public ValueTask<AccessibilityState?> GetAsync(string workspaceId, Guid runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Load(workspaceId, runId));
    }

    private async ValueTask<AccessibilityState> Mutate(string workspaceId, Guid runId, long expectedRevision,
        Guid operationId, string fingerprint, string payload, string operation, string actor, string reason,
        DateTimeOffset at, Func<AccessibilityState, AccessibilityState> mutation,
        Action<SqliteConnection, SqliteTransaction, AccessibilityState> sideEffect, CancellationToken ct)
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
            var current = Load(workspaceId, runId) ?? throw new AccessibilityValidationException("Accessibility run not found.");
            if (current.Revision != expectedRevision) throw new AccessibilityConflictException("Stale accessibility revision.");
            var next = mutation(current);
            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            var affected = Exec(connection, tx,
                "UPDATE accessibility_runs SET evidence_json=$e,status=$s,revision=$v,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND run_id=$id AND revision=$expected",
                ("$e", JsonSerializer.Serialize(next.Evidence)), ("$s", EnumText(next.Status)), ("$v", next.Revision),
                ("$m", next.MessageId!.Value.ToString("D")), ("$at", at.ToString("O")), ("$w", workspaceId),
                ("$id", runId.ToString("D")), ("$expected", expectedRevision));
            if (affected != 1) throw new AccessibilityConflictException("Stale accessibility revision.");
            sideEffect(connection, tx, next);
            ReplaceEvidence(connection, tx, next);
            History(connection, tx, next, operation, actor, reason, at);
            Receipt(connection, tx, workspaceId, operationId, runId, fingerprint, payload, next, at);
            Outbox(connection, tx, next, $"accessibility.{next.Status.ToString().ToLowerInvariant()}", at);
            tx.Commit();
            return next;
        }
        finally { _gate.Release(); }
    }

    private AccessibilityState? Load(string workspaceId, Guid runId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM accessibility_history WHERE workspace_id=$w AND run_id=$id ORDER BY revision DESC LIMIT 1";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$id", runId.ToString("D"));
        var json = command.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<AccessibilityState>(json)
            ?? throw new AccessibilityConflictException("Invalid persisted accessibility state.");
    }

    private (Guid RunId, string Fingerprint, string Payload, AccessibilityState State)? LoadReceipt(string workspaceId, Guid operationId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id,request_fingerprint,payload_digest,response_json FROM accessibility_receipts WHERE workspace_id=$w AND operation_id=$o";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$o", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var state = JsonSerializer.Deserialize<AccessibilityState>(reader.GetString(3))
            ?? throw new AccessibilityConflictException("Invalid persisted accessibility receipt.");
        return (Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), state);
    }

    private static void RequireReplay((Guid RunId, string Fingerprint, string Payload, AccessibilityState State) replay,
        Guid runId, string fingerprint, string payload)
    {
        if (replay.RunId != runId || !StringComparer.Ordinal.Equals(replay.Fingerprint, fingerprint)
            || !StringComparer.Ordinal.Equals(replay.Payload, payload))
            throw new AccessibilityConflictException("Operation reused with a different payload.");
    }

    private static void ValidateEvidence(AccessibilityEvidence evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence.EvidenceDigest) || evidence.Executions.Count == 0 ||
            evidence.Executions.Select(x => $"{x.AnalyzerId}@{x.AnalyzerVersion}").Distinct(StringComparer.Ordinal).Count() != evidence.Executions.Count)
            throw new AccessibilityValidationException("Accessibility evidence is invalid.");
    }

    private static void ReplaceEvidence(SqliteConnection connection, SqliteTransaction tx, AccessibilityState state)
    {
        foreach (var table in new[] { "accessibility_executions", "accessibility_findings", "accessibility_reviews", "accessibility_waivers" })
            Exec(connection, tx, $"DELETE FROM {table} WHERE workspace_id=$w AND run_id=$r", ("$w", state.WorkspaceId), ("$r", state.RunId.ToString("D")));
        PersistEvidence(connection, tx, state);
    }

    private static void PersistEvidence(SqliteConnection connection, SqliteTransaction tx, AccessibilityState state)
    {
        foreach (var x in state.Evidence.Executions)
            Exec(connection, tx, "INSERT INTO accessibility_executions VALUES($w,$r,$id,$v,$p,$i,$o,$c)",
                ("$w", state.WorkspaceId), ("$r", state.RunId.ToString("D")), ("$id", x.AnalyzerId), ("$v", x.AnalyzerVersion),
                ("$p", x.RuleProfile), ("$i", x.InputDigest), ("$o", x.OutputDigest), ("$c", x.FindingCount));
        foreach (var x in state.Evidence.Findings)
            Exec(connection, tx, "INSERT INTO accessibility_findings VALUES($w,$r,$id,$rule,$cat,$sev,$loc,$ed,$status,$json)",
                ("$w", state.WorkspaceId), ("$r", state.RunId.ToString("D")), ("$id", x.FindingId.ToString("D")),
                ("$rule", x.RuleId), ("$cat", EnumText(x.Category)), ("$sev", EnumText(x.Severity)), ("$loc", x.Location),
                ("$ed", x.EvidenceDigest), ("$status", EnumText(x.RemediationStatus)), ("$json", JsonSerializer.Serialize(x)));
        foreach (var x in state.Evidence.ManualReviews)
            Exec(connection, tx, "INSERT INTO accessibility_reviews VALUES($w,$r,$id,$scope,$reviewer,$ed,$d,$c,$json)",
                ("$w", state.WorkspaceId), ("$r", state.RunId.ToString("D")), ("$id", x.ReviewId.ToString("D")),
                ("$scope", x.Scope), ("$reviewer", x.Reviewer), ("$ed", x.EvidenceDigest), ("$d", EnumText(x.Disposition)),
                ("$c", x.Completed ? 1 : 0), ("$json", JsonSerializer.Serialize(x)));
        foreach (var x in state.Evidence.Waivers)
            Exec(connection, tx, "INSERT INTO accessibility_waivers VALUES($w,$r,$id,$f,$expires,$by,$ed,$json)",
                ("$w", state.WorkspaceId), ("$r", state.RunId.ToString("D")), ("$id", x.WaiverId.ToString("D")),
                ("$f", x.FindingId.ToString("D")), ("$expires", x.ExpiresAtUtc.ToString("O")), ("$by", x.ApprovedBy),
                ("$ed", x.EvidenceDigest), ("$json", JsonSerializer.Serialize(x)));
    }

    private static void PersistDecision(SqliteConnection connection, SqliteTransaction tx, AccessibilityDecisionCommand command,
        AccessibilityState state, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO accessibility_decisions VALUES($w,$r,$o,$d,$reason,$e,$ed,$actor,$v,$at)",
        ("$w", command.WorkspaceId), ("$r", command.RunId.ToString("D")), ("$o", command.RequestId.ToString("D")),
        ("$d", EnumText(command.Decision)), ("$reason", command.Reason), ("$e", command.Evidence),
        ("$ed", command.EvidenceDigest), ("$actor", command.Actor), ("$v", state.Revision), ("$at", at.ToString("O")));

    private static void History(SqliteConnection connection, SqliteTransaction tx, AccessibilityState state,
        string operation, string actor, string reason, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO accessibility_history VALUES($w,$r,$v,$o,$actor,$reason,$json,$at)",
        ("$w", state.WorkspaceId), ("$r", state.RunId.ToString("D")), ("$v", state.Revision), ("$o", operation),
        ("$actor", actor), ("$reason", reason), ("$json", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Receipt(SqliteConnection connection, SqliteTransaction tx, string workspaceId, Guid operationId,
        Guid runId, string fingerprint, string payload, AccessibilityState state, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO accessibility_receipts VALUES($w,$o,$r,$f,$p,$json,$at)",
        ("$w", workspaceId), ("$o", operationId.ToString("D")), ("$r", runId.ToString("D")),
        ("$f", fingerprint), ("$p", payload), ("$json", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Outbox(SqliteConnection connection, SqliteTransaction tx, AccessibilityState state,
        string eventType, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO accessibility_outbox VALUES($m,$w,$r,$v,$e,$p,$at)",
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
    private static Guid MessageId(Guid runId, long revision) => DeterministicGuid($"accessibility:{runId:D}:{revision}");
    private static Guid DeterministicGuid(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        _gate.Release();
        _gate.Dispose();
    }
}

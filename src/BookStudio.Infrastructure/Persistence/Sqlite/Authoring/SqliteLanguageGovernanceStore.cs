using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteLanguageGovernanceStore : ILanguageGovernanceStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteLanguageGovernanceStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async ValueTask<LanguagePolicySubmissionResult> SubmitAsync(
        LanguagePolicyRequest request, DateTimeOffset at, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var payload = Digest(JsonSerializer.Serialize(request));
            var replay = LoadReceipt(request.WorkspaceId, request.RequestId);
            if (replay is not null)
            {
                RequireReplay(replay.Value, request.PolicyId, request.RequestFingerprint, payload);
                return new LanguagePolicySubmissionResult(replay.Value.State, true);
            }

            if (Load(request.WorkspaceId, request.PolicyId) is not null)
                throw new LanguageGovernanceConflictException("Language policy already exists.");

            var policyDigest = Digest(JsonSerializer.Serialize(new
            {
                request.ProjectId,
                request.WorkspaceId,
                request.BookLanguageTag,
                request.LocaleProfile,
                request.PolicyRevision,
                Scopes = request.AllowedSecondaryLanguageScopes.OrderBy(x => x.ScopeId, StringComparer.Ordinal)
            }));
            var state = new LanguageValidationState(request.PolicyId, request.ProjectId, request.WorkspaceId,
                request.UiLanguageTag, request.BookLanguageTag, request.LocaleProfile, request.PolicyRevision,
                policyDigest, request.AllowedSecondaryLanguageScopes.OrderBy(x => x.ScopeId, StringComparer.Ordinal).ToArray(),
                null, null, LanguageGovernanceStatus.Draft, 1, MessageId(request.PolicyId, 1), at, at);

            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            Exec(connection, tx,
                "INSERT INTO language_governance_policies(workspace_id,policy_id,project_id,authority_json,ui_language_tag,book_language_tag,locale_profile,policy_revision,policy_digest,allowed_scopes_json,compiled_contract_json,last_validation_json,status,revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,$ui,$book,$locale,$pr,$pd,$sc,NULL,NULL,$s,1,$m,$at,$at)",
                ("$w", request.WorkspaceId), ("$id", request.PolicyId.ToString("D")),
                ("$p", request.ProjectId.ToString("D")), ("$a", JsonSerializer.Serialize(request.Authority)),
                ("$ui", request.UiLanguageTag), ("$book", request.BookLanguageTag),
                ("$locale", request.LocaleProfile), ("$pr", request.PolicyRevision), ("$pd", policyDigest),
                ("$sc", JsonSerializer.Serialize(state.AllowedSecondaryLanguageScopes)), ("$s", state.Status.ToString()),
                ("$m", state.MessageId!.Value.ToString("D")), ("$at", at.ToString("O")));
            History(connection, tx, state, "SUBMIT", request.Actor, "Language policy submitted", at);
            Receipt(connection, tx, request.WorkspaceId, request.RequestId, request.PolicyId,
                request.RequestFingerprint, payload, state, at);
            Outbox(connection, tx, state, "language-governance.submitted", at);
            tx.Commit();
            return new LanguagePolicySubmissionResult(state, false);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<LanguageValidationState> RecordValidationAsync(
        LanguageValidationCommand command, CompiledLanguageContract compiledContract,
        LanguageValidationResult result, DateTimeOffset at, CancellationToken ct = default)
    {
        if (compiledContract is null || result is null)
            throw new LanguageGovernanceValidationException("Compiled language contract and validation result are required.");

        var payload = Digest(JsonSerializer.Serialize(new { command, compiledContract, result }));
        return Mutate(command.WorkspaceId, command.PolicyId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, payload, "VALIDATE", command.Actor, "Generated text language validated", at,
            state =>
            {
                if (state.Status is LanguageGovernanceStatus.Approved or LanguageGovernanceStatus.Rejected or LanguageGovernanceStatus.Superseded)
                    throw new LanguageGovernanceTransitionException("Terminal language governance state cannot be validated.");
                if (!StringComparer.Ordinal.Equals(state.PolicyDigest, compiledContract.PolicyDigest))
                    throw new LanguageGovernanceConflictException("Compiled contract is not bound to persisted policy digest.");
                return state with
                {
                    CompiledContract = compiledContract,
                    LastValidation = result,
                    Status = result.Accepted ? LanguageGovernanceStatus.Validated : LanguageGovernanceStatus.RetryRequired,
                    Revision = state.Revision + 1,
                    MessageId = MessageId(state.PolicyId, state.Revision + 1),
                    UpdatedAtUtc = at
                };
            },
            (connection, tx, next) => PersistFindings(connection, tx, next), ct);
    }

    public ValueTask<LanguageValidationState> DecideAsync(
        LanguageDecisionCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        var payload = Digest(JsonSerializer.Serialize(command));
        return Mutate(command.WorkspaceId, command.PolicyId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, payload, "DECIDE", command.Actor, command.Reason, at,
            state =>
            {
                if (command.Decision == LanguageDecision.Approve &&
                    (state.Status != LanguageGovernanceStatus.Validated || state.LastValidation?.Accepted != true))
                    throw new LanguageGovernanceTransitionException("Only accepted validated language evidence can be approved.");

                return state with
                {
                    Status = command.Decision switch
                    {
                        LanguageDecision.Approve => LanguageGovernanceStatus.Approved,
                        LanguageDecision.Reject => LanguageGovernanceStatus.Rejected,
                        LanguageDecision.Supersede => LanguageGovernanceStatus.Superseded,
                        _ => throw new LanguageGovernanceValidationException("Unsupported language governance decision.")
                    },
                    Revision = state.Revision + 1,
                    MessageId = MessageId(state.PolicyId, state.Revision + 1),
                    UpdatedAtUtc = at
                };
            },
            (connection, tx, next) => PersistDecision(connection, tx, command, next, at), ct);
    }

    public ValueTask<LanguageValidationState?> GetAsync(
        string workspaceId, Guid policyId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Load(workspaceId, policyId));
    }

    private async ValueTask<LanguageValidationState> Mutate(
        string workspaceId, Guid policyId, long expectedRevision, Guid operationId,
        string fingerprint, string payload, string operation, string actor, string reason,
        DateTimeOffset at, Func<LanguageValidationState, LanguageValidationState> mutation,
        Action<SqliteConnection, SqliteTransaction, LanguageValidationState> sideEffect,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var replay = LoadReceipt(workspaceId, operationId);
            if (replay is not null)
            {
                RequireReplay(replay.Value, policyId, fingerprint, payload);
                return replay.Value.State;
            }

            var current = Load(workspaceId, policyId)
                ?? throw new LanguageGovernanceValidationException("Language policy not found.");
            if (current.Revision != expectedRevision)
                throw new LanguageGovernanceConflictException("Stale language governance revision.");

            var next = mutation(current);
            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            var affected = Exec(connection, tx,
                "UPDATE language_governance_policies SET compiled_contract_json=$cc,last_validation_json=$lv,status=$s,revision=$r,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND policy_id=$id AND revision=$expected",
                ("$cc", next.CompiledContract is null ? null : JsonSerializer.Serialize(next.CompiledContract)),
                ("$lv", next.LastValidation is null ? null : JsonSerializer.Serialize(next.LastValidation)),
                ("$s", next.Status.ToString()), ("$r", next.Revision),
                ("$m", next.MessageId!.Value.ToString("D")), ("$at", at.ToString("O")),
                ("$w", workspaceId), ("$id", policyId.ToString("D")), ("$expected", expectedRevision));
            if (affected != 1)
                throw new LanguageGovernanceConflictException("Stale language governance revision.");

            sideEffect(connection, tx, next);
            History(connection, tx, next, operation, actor, reason, at);
            Receipt(connection, tx, workspaceId, operationId, policyId, fingerprint, payload, next, at);
            Outbox(connection, tx, next, $"language-governance.{next.Status.ToString().ToLowerInvariant()}", at);
            tx.Commit();
            return next;
        }
        finally { _gate.Release(); }
    }

    private LanguageValidationState? Load(string workspaceId, Guid policyId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM language_governance_history WHERE workspace_id=$w AND policy_id=$id ORDER BY revision DESC LIMIT 1";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$id", policyId.ToString("D"));
        var json = command.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<LanguageValidationState>(json)
            ?? throw new LanguageGovernanceConflictException("Invalid persisted language governance state.");
    }

    private (Guid PolicyId, string Fingerprint, string Payload, LanguageValidationState State)? LoadReceipt(
        string workspaceId, Guid operationId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT policy_id,request_fingerprint,payload_digest,response_json FROM language_governance_receipts WHERE workspace_id=$w AND operation_id=$o";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$o", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var state = JsonSerializer.Deserialize<LanguageValidationState>(reader.GetString(3))
            ?? throw new LanguageGovernanceConflictException("Invalid persisted language governance receipt.");
        return (Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), state);
    }

    private static void RequireReplay(
        (Guid PolicyId, string Fingerprint, string Payload, LanguageValidationState State) replay,
        Guid policyId, string fingerprint, string payload)
    {
        if (replay.PolicyId != policyId || !StringComparer.Ordinal.Equals(replay.Fingerprint, fingerprint) ||
            !StringComparer.Ordinal.Equals(replay.Payload, payload))
            throw new LanguageGovernanceConflictException("Operation reused with a different payload.");
    }

    private static void PersistFindings(SqliteConnection connection, SqliteTransaction tx, LanguageValidationState state)
    {
        if (state.LastValidation is null) return;
        foreach (var finding in state.LastValidation.Findings.OrderBy(x => x.FindingId, StringComparer.Ordinal))
            Exec(connection, tx,
                "INSERT INTO language_governance_findings VALUES($w,$id,$r,$f,$rule,$sev,$st,$len,$exp,$det,$c,$e,$covered,$j)",
                ("$w", state.WorkspaceId), ("$id", state.PolicyId.ToString("D")), ("$r", state.Revision),
                ("$f", finding.FindingId), ("$rule", finding.RuleId), ("$sev", finding.Severity.ToString()),
                ("$st", finding.Start), ("$len", finding.Length), ("$exp", finding.ExpectedLanguageTag),
                ("$det", finding.DetectedLanguageTag), ("$c", finding.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("$e", finding.EvidenceDigest), ("$covered", finding.CoveredByApprovedScope ? 1 : 0),
                ("$j", JsonSerializer.Serialize(finding)));
    }

    private static void PersistDecision(SqliteConnection connection, SqliteTransaction tx,
        LanguageDecisionCommand command, LanguageValidationState state, DateTimeOffset at) =>
        Exec(connection, tx,
            "INSERT INTO language_governance_decisions VALUES($w,$id,$o,$d,$reason,$e,$ed,$a,$r,$at)",
            ("$w", command.WorkspaceId), ("$id", command.PolicyId.ToString("D")),
            ("$o", command.RequestId.ToString("D")), ("$d", command.Decision.ToString()),
            ("$reason", command.Reason), ("$e", command.Evidence), ("$ed", command.EvidenceDigest),
            ("$a", command.Actor), ("$r", state.Revision), ("$at", at.ToString("O")));

    private static void History(SqliteConnection connection, SqliteTransaction tx,
        LanguageValidationState state, string operation, string actor, string reason, DateTimeOffset at) =>
        Exec(connection, tx,
            "INSERT INTO language_governance_history VALUES($w,$id,$r,$o,$a,$reason,$j,$at)",
            ("$w", state.WorkspaceId), ("$id", state.PolicyId.ToString("D")), ("$r", state.Revision),
            ("$o", operation), ("$a", actor), ("$reason", reason),
            ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Receipt(SqliteConnection connection, SqliteTransaction tx, string workspaceId,
        Guid operationId, Guid policyId, string fingerprint, string payload, LanguageValidationState state,
        DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO language_governance_receipts VALUES($w,$o,$id,$f,$d,$j,$at)",
        ("$w", workspaceId), ("$o", operationId.ToString("D")), ("$id", policyId.ToString("D")),
        ("$f", fingerprint), ("$d", payload), ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Outbox(SqliteConnection connection, SqliteTransaction tx,
        LanguageValidationState state, string eventType, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO language_governance_outbox VALUES($m,$w,$id,$r,$e,$j,$at,NULL)",
        ("$m", state.MessageId!.Value.ToString("D")), ("$w", state.WorkspaceId),
        ("$id", state.PolicyId.ToString("D")), ("$r", state.Revision), ("$e", eventType),
        ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static int Exec(SqliteConnection connection, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Guid MessageId(Guid policyId, long revision)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"language-governance|{policyId:D}|{revision}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

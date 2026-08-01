using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Publishing;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Publishing;

public sealed class SqliteProfessionalReleaseStore : IProfessionalReleaseStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteProfessionalReleaseStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async ValueTask<ProfessionalReleaseSubmissionResult> SubmitAsync(
        ProfessionalReleaseRequest request, DateTimeOffset at, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var payload = Digest(JsonSerializer.Serialize(request));
            var replay = LoadReceipt(request.WorkspaceId, request.RequestId);
            if (replay is not null)
            {
                RequireReplay(replay.Value, request.ReleaseId, request.RequestFingerprint, payload);
                return new ProfessionalReleaseSubmissionResult(replay.Value.State, true);
            }

            if (Load(request.WorkspaceId, request.ReleaseId) is not null)
                throw new ProfessionalReleaseConflictException("Professional release already exists.");

            var artifacts = request.Artifacts
                .Select(x => new VerifiedReleaseArtifact(x.LogicalName, x.MediaType, x.ByteLength, x.Digest,
                    x.Provenance, x.SourceAuthority, x.Required))
                .OrderBy(x => x.LogicalName, StringComparer.Ordinal)
                .ThenBy(x => x.MediaType, StringComparer.Ordinal)
                .ThenBy(x => x.Digest, StringComparer.Ordinal)
                .ToArray();

            var state = new ProfessionalReleaseState(request.ReleaseId, request.ProjectId, request.WorkspaceId,
                request.Authority, request.Channel, request.SemanticVersion, request.Locale,
                request.SupersedesReleaseId, artifacts, null, null, null, ProfessionalReleaseStatus.Draft,
                1, MessageId(request.ReleaseId, 1), at, at);

            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            Exec(connection, tx,
                "INSERT INTO professional_releases(workspace_id,release_id,project_id,authority_json,channel,semantic_version,locale,supersedes_release_id,artifacts_json,manifest_json,inventory_digest,evidence_digest,status,revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,$c,$v,$l,$sp,$ar,NULL,NULL,NULL,$s,1,$m,$at,$at)",
                ("$w", request.WorkspaceId), ("$id", request.ReleaseId.ToString("D")),
                ("$p", request.ProjectId.ToString("D")), ("$a", JsonSerializer.Serialize(request.Authority)),
                ("$c", request.Channel), ("$v", request.SemanticVersion), ("$l", request.Locale),
                ("$sp", request.SupersedesReleaseId?.ToString("D")), ("$ar", JsonSerializer.Serialize(artifacts)),
                ("$s", state.Status.ToString()), ("$m", state.MessageId!.Value.ToString("D")), ("$at", at.ToString("O")));
            History(connection, tx, state, "SUBMIT", request.Actor, "Professional release submitted", at);
            Receipt(connection, tx, request.WorkspaceId, request.RequestId, request.ReleaseId,
                request.RequestFingerprint, payload, state, at);
            Outbox(connection, tx, state, "professional-release.submitted", at);
            tx.Commit();
            return new ProfessionalReleaseSubmissionResult(state, false);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<ProfessionalReleaseState> FreezeAsync(ProfessionalReleaseFreezeCommand command,
        IReadOnlyList<VerifiedReleaseArtifact> artifacts, ProfessionalReleaseManifest manifest,
        string inventoryDigest, string evidenceDigest, DateTimeOffset at, CancellationToken ct = default)
    {
        if (artifacts is null || artifacts.Count == 0 || manifest is null ||
            string.IsNullOrWhiteSpace(inventoryDigest) || string.IsNullOrWhiteSpace(evidenceDigest))
            throw new ProfessionalReleaseValidationException("Frozen release evidence is invalid.");

        var ordered = artifacts.OrderBy(x => x.LogicalName, StringComparer.Ordinal)
            .ThenBy(x => x.MediaType, StringComparer.Ordinal)
            .ThenBy(x => x.Digest, StringComparer.Ordinal).ToArray();
        var payload = Digest(JsonSerializer.Serialize(new
        {
            Command = command, Artifacts = ordered, Manifest = manifest, InventoryDigest = inventoryDigest,
            EvidenceDigest = evidenceDigest
        }));

        return Mutate(command.WorkspaceId, command.ReleaseId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, payload, "FREEZE", command.Actor, "Professional release frozen", at,
            state =>
            {
                if (state.Status != ProfessionalReleaseStatus.Draft)
                    throw new ProfessionalReleaseTransitionException("Only a draft release can be frozen.");
                return state with
                {
                    Artifacts = ordered, Manifest = manifest, InventoryDigest = inventoryDigest,
                    EvidenceDigest = evidenceDigest, Status = ProfessionalReleaseStatus.Frozen,
                    Revision = state.Revision + 1, MessageId = MessageId(state.ReleaseId, state.Revision + 1),
                    UpdatedAtUtc = at
                };
            }, (connection, tx, next) => PersistFreeze(connection, tx, next), ct);
    }

    public ValueTask<ProfessionalReleaseState> DecideAsync(
        ProfessionalReleaseDecisionCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.ReleaseId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, Digest(JsonSerializer.Serialize(command)), "DECIDE", command.Actor,
            command.Reason, at,
            state => state with
            {
                Status = command.Decision switch
                {
                    ProfessionalReleaseDecision.Approve => ProfessionalReleaseStatus.Approved,
                    ProfessionalReleaseDecision.Reject => ProfessionalReleaseStatus.Rejected,
                    ProfessionalReleaseDecision.Supersede => ProfessionalReleaseStatus.Superseded,
                    _ => throw new ProfessionalReleaseValidationException("Unsupported professional release decision.")
                },
                Revision = state.Revision + 1,
                MessageId = MessageId(state.ReleaseId, state.Revision + 1),
                UpdatedAtUtc = at
            }, (connection, tx, next) => PersistDecision(connection, tx, command, next, at), ct);

    public ValueTask<ProfessionalReleaseState?> GetAsync(
        string workspaceId, Guid releaseId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Load(workspaceId, releaseId));
    }

    private async ValueTask<ProfessionalReleaseState> Mutate(string workspaceId, Guid releaseId,
        long expectedRevision, Guid operationId, string fingerprint, string payload, string operation,
        string actor, string reason, DateTimeOffset at, Func<ProfessionalReleaseState, ProfessionalReleaseState> mutation,
        Action<SqliteConnection, SqliteTransaction, ProfessionalReleaseState> sideEffect, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var replay = LoadReceipt(workspaceId, operationId);
            if (replay is not null)
            {
                RequireReplay(replay.Value, releaseId, fingerprint, payload);
                return replay.Value.State;
            }

            var current = Load(workspaceId, releaseId)
                ?? throw new ProfessionalReleaseValidationException("Professional release not found.");
            if (current.Revision != expectedRevision)
                throw new ProfessionalReleaseConflictException("Stale professional release revision.");

            var next = mutation(current);
            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            var affected = Exec(connection, tx,
                "UPDATE professional_releases SET artifacts_json=$ar,manifest_json=$mj,inventory_digest=$id,evidence_digest=$ed,status=$s,revision=$r,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND release_id=$rel AND revision=$expected",
                ("$ar", JsonSerializer.Serialize(next.Artifacts)),
                ("$mj", next.Manifest is null ? null : JsonSerializer.Serialize(next.Manifest)),
                ("$id", next.InventoryDigest), ("$ed", next.EvidenceDigest), ("$s", next.Status.ToString()),
                ("$r", next.Revision), ("$m", next.MessageId!.Value.ToString("D")), ("$at", at.ToString("O")),
                ("$w", workspaceId), ("$rel", releaseId.ToString("D")), ("$expected", expectedRevision));
            if (affected != 1)
                throw new ProfessionalReleaseConflictException("Stale professional release revision.");

            sideEffect(connection, tx, next);
            History(connection, tx, next, operation, actor, reason, at);
            Receipt(connection, tx, workspaceId, operationId, releaseId, fingerprint, payload, next, at);
            Outbox(connection, tx, next, $"professional-release.{next.Status.ToString().ToLowerInvariant()}", at);
            tx.Commit();
            return next;
        }
        finally { _gate.Release(); }
    }

    private ProfessionalReleaseState? Load(string workspaceId, Guid releaseId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM professional_release_history WHERE workspace_id=$w AND release_id=$id ORDER BY revision DESC LIMIT 1";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$id", releaseId.ToString("D"));
        var json = command.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<ProfessionalReleaseState>(json)
            ?? throw new ProfessionalReleaseConflictException("Invalid persisted professional release state.");
    }

    private (Guid ReleaseId, string Fingerprint, string Payload, ProfessionalReleaseState State)? LoadReceipt(
        string workspaceId, Guid operationId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT release_id,request_fingerprint,payload_digest,response_json FROM professional_release_receipts WHERE workspace_id=$w AND operation_id=$o";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$o", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var state = JsonSerializer.Deserialize<ProfessionalReleaseState>(reader.GetString(3))
            ?? throw new ProfessionalReleaseConflictException("Invalid persisted professional release receipt.");
        return (Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), state);
    }

    private static void RequireReplay(
        (Guid ReleaseId, string Fingerprint, string Payload, ProfessionalReleaseState State) replay,
        Guid releaseId, string fingerprint, string payload)
    {
        if (replay.ReleaseId != releaseId || !StringComparer.Ordinal.Equals(replay.Fingerprint, fingerprint) ||
            !StringComparer.Ordinal.Equals(replay.Payload, payload))
            throw new ProfessionalReleaseConflictException("Operation reused with a different payload.");
    }

    private static void PersistFreeze(SqliteConnection connection, SqliteTransaction tx,
        ProfessionalReleaseState state)
    {
        foreach (var artifact in state.Artifacts)
            Exec(connection, tx,
                "INSERT INTO professional_release_artifacts VALUES($w,$id,$r,$n,$mt,$b,$d,$p,$sa,$req,$j)",
                ("$w", state.WorkspaceId), ("$id", state.ReleaseId.ToString("D")), ("$r", state.Revision),
                ("$n", artifact.LogicalName), ("$mt", artifact.MediaType), ("$b", artifact.ByteLength),
                ("$d", artifact.Digest), ("$p", artifact.Provenance), ("$sa", artifact.SourceAuthority),
                ("$req", artifact.Required ? 1 : 0), ("$j", JsonSerializer.Serialize(artifact)));

        Exec(connection, tx,
            "INSERT INTO professional_release_manifests VALUES($w,$id,$r,$md,$i,$e,$j,$at)",
            ("$w", state.WorkspaceId), ("$id", state.ReleaseId.ToString("D")), ("$r", state.Revision),
            ("$md", state.Manifest!.ManifestDigest), ("$i", state.InventoryDigest), ("$e", state.EvidenceDigest),
            ("$j", JsonSerializer.Serialize(state.Manifest)), ("$at", state.Manifest.FrozenAtUtc.ToString("O")));
    }

    private static void PersistDecision(SqliteConnection connection, SqliteTransaction tx,
        ProfessionalReleaseDecisionCommand command, ProfessionalReleaseState state, DateTimeOffset at) =>
        Exec(connection, tx,
            "INSERT INTO professional_release_decisions VALUES($w,$id,$o,$d,$r,$e,$ed,$a,$v,$at)",
            ("$w", command.WorkspaceId), ("$id", command.ReleaseId.ToString("D")),
            ("$o", command.RequestId.ToString("D")), ("$d", command.Decision.ToString()),
            ("$r", command.Reason), ("$e", command.Evidence), ("$ed", command.EvidenceDigest),
            ("$a", command.Actor), ("$v", state.Revision), ("$at", at.ToString("O")));

    private static void History(SqliteConnection connection, SqliteTransaction tx,
        ProfessionalReleaseState state, string operation, string actor, string reason, DateTimeOffset at) =>
        Exec(connection, tx,
            "INSERT INTO professional_release_history VALUES($w,$id,$r,$o,$a,$reason,$j,$at)",
            ("$w", state.WorkspaceId), ("$id", state.ReleaseId.ToString("D")), ("$r", state.Revision),
            ("$o", operation), ("$a", actor), ("$reason", reason),
            ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Receipt(SqliteConnection connection, SqliteTransaction tx, string workspaceId,
        Guid operationId, Guid releaseId, string fingerprint, string payload, ProfessionalReleaseState state,
        DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO professional_release_receipts VALUES($w,$o,$id,$f,$d,$j,$at)",
        ("$w", workspaceId), ("$o", operationId.ToString("D")), ("$id", releaseId.ToString("D")),
        ("$f", fingerprint), ("$d", payload), ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Outbox(SqliteConnection connection, SqliteTransaction tx,
        ProfessionalReleaseState state, string eventType, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO professional_release_outbox VALUES($m,$w,$id,$r,$e,$j,$at,NULL)",
        ("$m", state.MessageId!.Value.ToString("D")), ("$w", state.WorkspaceId),
        ("$id", state.ReleaseId.ToString("D")), ("$r", state.Revision), ("$e", eventType),
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

    private static Guid MessageId(Guid releaseId, long revision)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"professional-release|{releaseId:D}|{revision}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

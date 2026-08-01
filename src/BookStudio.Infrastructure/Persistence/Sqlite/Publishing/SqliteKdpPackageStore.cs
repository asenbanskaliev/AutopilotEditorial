using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Publishing;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Publishing;

public sealed class SqliteKdpPackageStore : IKdpPackageStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteKdpPackageStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async ValueTask<KdpPackageSubmissionResult> SubmitAsync(KdpPackageRequest request, DateTimeOffset at, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var payload = Digest(JsonSerializer.Serialize(request));
            var replay = LoadReceipt(request.WorkspaceId, request.RequestId);
            if (replay is not null)
            {
                RequireReplay(replay.Value, request.PackageId, request.RequestFingerprint, payload);
                return new KdpPackageSubmissionResult(replay.Value.State, true);
            }
            if (Load(request.WorkspaceId, request.PackageId) is not null)
                throw new KdpPackageConflictException("KDP package already exists.");

            var state = new KdpPackageState(request.PackageId, request.ProjectId, request.WorkspaceId,
                request.Authority, request.Metadata, request.Artifacts, request.Marketplace, request.Language,
                request.FormatProfile, request.ProfileVersion, null, Array.Empty<KdpMetadataFinding>(), null,
                KdpPackageStatus.Draft, 1, MessageId(request.PackageId, 1), at, at);

            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            Exec(connection, tx,
                "INSERT INTO kdp_packages(workspace_id,package_id,project_id,authority_json,metadata_json,artifacts_json,marketplace,language,format_profile,profile_version,manifest_json,findings_json,evidence_digest,status,revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,$md,$ar,$mk,$l,$fp,$pv,NULL,$f,NULL,$s,1,$m,$at,$at)",
                ("$w", request.WorkspaceId), ("$id", request.PackageId.ToString("D")), ("$p", request.ProjectId.ToString("D")),
                ("$a", JsonSerializer.Serialize(request.Authority)), ("$md", JsonSerializer.Serialize(request.Metadata)),
                ("$ar", JsonSerializer.Serialize(request.Artifacts)), ("$mk", request.Marketplace), ("$l", request.Language),
                ("$fp", request.FormatProfile), ("$pv", request.ProfileVersion), ("$f", "[]"),
                ("$s", state.Status.ToString()), ("$m", state.MessageId!.Value.ToString("D")), ("$at", at.ToString("O")));
            PersistMetadata(connection, tx, state, at);
            History(connection, tx, state, "SUBMIT", request.Actor, "KDP package submitted", at);
            Receipt(connection, tx, request.WorkspaceId, request.RequestId, request.PackageId, request.RequestFingerprint, payload, state, at);
            Outbox(connection, tx, state, "kdp-package.submitted", at);
            tx.Commit();
            return new KdpPackageSubmissionResult(state, false);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<KdpPackageState> EvaluateAsync(KdpPackageEvaluationCommand command, KdpPackageManifest manifest,
        IReadOnlyList<KdpMetadataFinding> findings, string evidenceDigest, DateTimeOffset at, CancellationToken ct = default)
    {
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.ManifestDigest) || string.IsNullOrWhiteSpace(manifest.PackageDigest)
            || string.IsNullOrWhiteSpace(manifest.CanonicalJson) || string.IsNullOrWhiteSpace(evidenceDigest))
            throw new KdpPackageValidationException("KDP package evidence is invalid.");
        var ordered = findings.OrderBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.FindingId).ToArray();
        var payload = Digest(JsonSerializer.Serialize(new { Command = command, Manifest = manifest, Findings = ordered, EvidenceDigest = evidenceDigest }));
        return Mutate(command.WorkspaceId, command.PackageId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, payload, "EVALUATE", command.Actor, "KDP package evaluated", at,
            state =>
            {
                if (state.Status is not KdpPackageStatus.Draft and not KdpPackageStatus.RepairRequired)
                    throw new KdpPackageTransitionException("Only draft or repair-required packages can be evaluated.");
                return state with { Manifest = manifest, Findings = ordered, EvidenceDigest = evidenceDigest,
                    Status = KdpPackageStatus.Evaluated, Revision = state.Revision + 1,
                    MessageId = MessageId(state.PackageId, state.Revision + 1), UpdatedAtUtc = at };
            }, (connection, tx, next) => PersistEvaluation(connection, tx, next, at), ct);
    }

    public ValueTask<KdpPackageState> DecideAsync(KdpPackageDecisionCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.PackageId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, Digest(JsonSerializer.Serialize(command)), "DECIDE", command.Actor, command.Reason, at,
            state => state with
            {
                Status = command.Decision switch
                {
                    KdpPackageDecision.Approve => KdpPackageStatus.Approved,
                    KdpPackageDecision.ReturnToRepair => KdpPackageStatus.RepairRequired,
                    KdpPackageDecision.Reject => KdpPackageStatus.Rejected,
                    KdpPackageDecision.Supersede => KdpPackageStatus.Superseded,
                    _ => throw new KdpPackageValidationException("Unsupported KDP package decision.")
                },
                Revision = state.Revision + 1,
                MessageId = MessageId(state.PackageId, state.Revision + 1),
                UpdatedAtUtc = at
            }, (connection, tx, next) => PersistDecision(connection, tx, command, next, at), ct);

    public ValueTask<KdpPackageState?> GetAsync(string workspaceId, Guid packageId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Load(workspaceId, packageId));
    }

    private async ValueTask<KdpPackageState> Mutate(string workspaceId, Guid packageId, long expectedRevision,
        Guid operationId, string fingerprint, string payload, string operation, string actor, string reason,
        DateTimeOffset at, Func<KdpPackageState, KdpPackageState> mutation,
        Action<SqliteConnection, SqliteTransaction, KdpPackageState> sideEffect, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var replay = LoadReceipt(workspaceId, operationId);
            if (replay is not null)
            {
                RequireReplay(replay.Value, packageId, fingerprint, payload);
                return replay.Value.State;
            }
            var current = Load(workspaceId, packageId) ?? throw new KdpPackageValidationException("KDP package not found.");
            if (current.Revision != expectedRevision) throw new KdpPackageConflictException("Stale KDP package revision.");
            var next = mutation(current);
            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            var affected = Exec(connection, tx,
                "UPDATE kdp_packages SET manifest_json=$mj,findings_json=$f,evidence_digest=$ed,status=$s,revision=$v,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND package_id=$id AND revision=$expected",
                ("$mj", next.Manifest is null ? null : JsonSerializer.Serialize(next.Manifest)),
                ("$f", JsonSerializer.Serialize(next.Findings)), ("$ed", next.EvidenceDigest), ("$s", next.Status.ToString()),
                ("$v", next.Revision), ("$m", next.MessageId!.Value.ToString("D")), ("$at", at.ToString("O")),
                ("$w", workspaceId), ("$id", packageId.ToString("D")), ("$expected", expectedRevision));
            if (affected != 1) throw new KdpPackageConflictException("Stale KDP package revision.");
            sideEffect(connection, tx, next);
            History(connection, tx, next, operation, actor, reason, at);
            Receipt(connection, tx, workspaceId, operationId, packageId, fingerprint, payload, next, at);
            Outbox(connection, tx, next, $"kdp-package.{next.Status.ToString().ToLowerInvariant()}", at);
            tx.Commit();
            return next;
        }
        finally { _gate.Release(); }
    }

    private KdpPackageState? Load(string workspaceId, Guid packageId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM kdp_package_history WHERE workspace_id=$w AND package_id=$id ORDER BY revision DESC LIMIT 1";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$id", packageId.ToString("D"));
        var json = command.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<KdpPackageState>(json)
            ?? throw new KdpPackageConflictException("Invalid persisted KDP package state.");
    }

    private (Guid PackageId, string Fingerprint, string Payload, KdpPackageState State)? LoadReceipt(string workspaceId, Guid operationId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT package_id,request_fingerprint,payload_digest,response_json FROM kdp_package_receipts WHERE workspace_id=$w AND operation_id=$o";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$o", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var state = JsonSerializer.Deserialize<KdpPackageState>(reader.GetString(3))
            ?? throw new KdpPackageConflictException("Invalid persisted KDP package receipt.");
        return (Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), state);
    }

    private static void RequireReplay((Guid PackageId, string Fingerprint, string Payload, KdpPackageState State) replay,
        Guid packageId, string fingerprint, string payload)
    {
        if (replay.PackageId != packageId || !StringComparer.Ordinal.Equals(replay.Fingerprint, fingerprint)
            || !StringComparer.Ordinal.Equals(replay.Payload, payload))
            throw new KdpPackageConflictException("Operation reused with a different payload.");
    }

    private static void PersistEvaluation(SqliteConnection connection, SqliteTransaction tx, KdpPackageState state, DateTimeOffset at)
    {
        Exec(connection, tx, "DELETE FROM kdp_package_findings WHERE workspace_id=$w AND package_id=$p",
            ("$w", state.WorkspaceId), ("$p", state.PackageId.ToString("D")));
        foreach (var finding in state.Findings)
            Exec(connection, tx, "INSERT INTO kdp_package_findings VALUES($w,$p,$id,$c,$s,$f,$r,$e,$st,$j)",
                ("$w", state.WorkspaceId), ("$p", state.PackageId.ToString("D")), ("$id", finding.FindingId.ToString("D")),
                ("$c", finding.Code), ("$s", finding.Severity.ToString()), ("$f", finding.Field), ("$r", finding.RuleId),
                ("$e", finding.EvidenceDigest), ("$st", finding.Status.ToString()), ("$j", JsonSerializer.Serialize(finding)));
        if (state.Manifest is not null)
            Exec(connection, tx, "INSERT INTO kdp_package_manifests VALUES($w,$p,$v,$md,$pd,$cj,$mj,$at)",
                ("$w", state.WorkspaceId), ("$p", state.PackageId.ToString("D")), ("$v", state.Revision),
                ("$md", state.Manifest.ManifestDigest), ("$pd", state.Manifest.PackageDigest),
                ("$cj", state.Manifest.CanonicalJson), ("$mj", JsonSerializer.Serialize(state.Manifest)), ("$at", at.ToString("O")));
    }

    private static void PersistMetadata(SqliteConnection connection, SqliteTransaction tx, KdpPackageState state, DateTimeOffset at) =>
        Exec(connection, tx, "INSERT INTO kdp_package_metadata_revisions VALUES($w,$p,$v,$m,$pv,$d,$at)",
            ("$w", state.WorkspaceId), ("$p", state.PackageId.ToString("D")), ("$v", state.Revision),
            ("$m", JsonSerializer.Serialize(state.Metadata)), ("$pv", state.ProfileVersion),
            ("$d", Digest(JsonSerializer.Serialize(state.Metadata))), ("$at", at.ToString("O")));

    private static void PersistDecision(SqliteConnection connection, SqliteTransaction tx, KdpPackageDecisionCommand command,
        KdpPackageState state, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO kdp_package_decisions VALUES($w,$p,$o,$d,$r,$e,$ed,$a,$v,$at)",
        ("$w", command.WorkspaceId), ("$p", command.PackageId.ToString("D")), ("$o", command.RequestId.ToString("D")),
        ("$d", command.Decision.ToString()), ("$r", command.Reason), ("$e", command.Evidence),
        ("$ed", command.EvidenceDigest), ("$a", command.Actor), ("$v", state.Revision), ("$at", at.ToString("O")));

    private static void History(SqliteConnection connection, SqliteTransaction tx, KdpPackageState state,
        string operation, string actor, string reason, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO kdp_package_history VALUES($w,$p,$v,$o,$a,$r,$j,$at)",
        ("$w", state.WorkspaceId), ("$p", state.PackageId.ToString("D")), ("$v", state.Revision),
        ("$o", operation), ("$a", actor), ("$r", reason), ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Receipt(SqliteConnection connection, SqliteTransaction tx, string workspaceId, Guid operationId,
        Guid packageId, string fingerprint, string payload, KdpPackageState state, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO kdp_package_receipts VALUES($w,$o,$p,$f,$d,$j,$at)",
        ("$w", workspaceId), ("$o", operationId.ToString("D")), ("$p", packageId.ToString("D")),
        ("$f", fingerprint), ("$d", payload), ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Outbox(SqliteConnection connection, SqliteTransaction tx, KdpPackageState state,
        string eventType, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO kdp_package_outbox VALUES($m,$w,$p,$v,$e,$j,$at)",
        ("$m", state.MessageId!.Value.ToString("D")), ("$w", state.WorkspaceId), ("$p", state.PackageId.ToString("D")),
        ("$v", state.Revision), ("$e", eventType), ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static int Exec(SqliteConnection connection, SqliteTransaction tx, string sql, params (string Name, object? Value)[] values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private static Guid MessageId(Guid packageId, long revision)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"kdp-package|{packageId:D}|{revision}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

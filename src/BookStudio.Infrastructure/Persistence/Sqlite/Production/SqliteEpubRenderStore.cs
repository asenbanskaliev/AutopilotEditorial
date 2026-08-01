using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Production;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Production;

public sealed class SqliteEpubRenderStore : IEpubRenderStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteEpubRenderStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async ValueTask<EpubRenderSubmissionResult> SubmitAsync(
        EpubRenderRequest request, EpubPackage package, DateTimeOffset at, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ValidatePackage(package);
            var payload = Digest(JsonSerializer.Serialize(new { Request = request, Package = package }));
            var replay = LoadReceipt(request.WorkspaceId, request.RequestId);
            if (replay is not null)
            {
                RequireReplay(replay.Value, request.RenderId, request.RequestFingerprint, payload);
                return new EpubRenderSubmissionResult(replay.Value.State, true);
            }
            if (Load(request.WorkspaceId, request.RenderId) is not null)
                throw new EpubRenderConflictException("EPUB render already exists.");

            var state = new EpubRenderState(request.RenderId, request.ProjectId, request.WorkspaceId,
                request.Manuscript, request.Profile, request.Metadata, package, [], EpubRenderStatus.Rendered,
                1, MessageId(request.RenderId, 1), at, at);

            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            Exec(connection, tx,
                "INSERT INTO epub_renders(workspace_id,render_id,project_id,manuscript_json,profile,metadata_json,package_json,findings_json,status,revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$m,$profile,$metadata,$package,'[]',$s,1,$message,$at,$at)",
                ("$w", request.WorkspaceId), ("$id", request.RenderId.ToString("D")),
                ("$p", request.ProjectId.ToString("D")), ("$m", JsonSerializer.Serialize(request.Manuscript)),
                ("$profile", EnumText(request.Profile)), ("$metadata", JsonSerializer.Serialize(request.Metadata)),
                ("$package", JsonSerializer.Serialize(package)), ("$s", EnumText(state.Status)),
                ("$message", state.MessageId!.Value.ToString("D")), ("$at", at.ToString("O")));
            PersistEntries(connection, tx, state);
            History(connection, tx, state, "SUBMIT", request.Actor, "EPUB package rendered and submitted", at);
            Receipt(connection, tx, request.WorkspaceId, request.RequestId, request.RenderId,
                request.RequestFingerprint, payload, state, at);
            Outbox(connection, tx, state, "epub.render.rendered", at);
            tx.Commit();
            return new EpubRenderSubmissionResult(state, false);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<EpubRenderState> ValidateAsync(
        EpubValidationCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.RenderId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, Digest(JsonSerializer.Serialize(command)), "VALIDATE", command.Actor,
            "EPUB validation recorded", at, state => state with
            {
                Findings = command.ExternalFindings,
                Status = command.ExternalFindings.Any(x => x.Severity == EpubSeverity.Blocking)
                    ? EpubRenderStatus.ReviewRequired : EpubRenderStatus.Validated,
                Revision = state.Revision + 1,
                MessageId = MessageId(state.RenderId, state.Revision + 1),
                UpdatedAtUtc = at
            }, static (_, _, _) => { }, ct);

    public ValueTask<EpubRenderState> DecideAsync(
        EpubDecisionCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.RenderId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, Digest(JsonSerializer.Serialize(command)), "DECIDE", command.Actor,
            command.Reason, at, state => state with
            {
                Status = command.Decision switch
                {
                    EpubDecision.Approve => EpubRenderStatus.Approved,
                    EpubDecision.ReturnToRepair => EpubRenderStatus.RepairRequired,
                    EpubDecision.Reject => EpubRenderStatus.Rejected,
                    EpubDecision.Supersede => EpubRenderStatus.Superseded,
                    _ => throw new EpubRenderValidationException("Unsupported EPUB decision.")
                },
                Revision = state.Revision + 1,
                MessageId = MessageId(state.RenderId, state.Revision + 1),
                UpdatedAtUtc = at
            }, (connection, tx, next) => PersistDecision(connection, tx, command, next, at), ct);

    public ValueTask<EpubRenderState?> GetAsync(string workspaceId, Guid renderId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Load(workspaceId, renderId));
    }

    private async ValueTask<EpubRenderState> Mutate(string workspaceId, Guid renderId, long expectedRevision,
        Guid operationId, string fingerprint, string payload, string operation, string actor, string reason,
        DateTimeOffset at, Func<EpubRenderState, EpubRenderState> mutation,
        Action<SqliteConnection, SqliteTransaction, EpubRenderState> sideEffect, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var replay = LoadReceipt(workspaceId, operationId);
            if (replay is not null)
            {
                RequireReplay(replay.Value, renderId, fingerprint, payload);
                return replay.Value.State;
            }
            var current = Load(workspaceId, renderId) ?? throw new EpubRenderValidationException("EPUB render not found.");
            if (current.Revision != expectedRevision) throw new EpubRenderConflictException("Stale EPUB render revision.");
            var next = mutation(current);
            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            var affected = Exec(connection, tx,
                "UPDATE epub_renders SET package_json=$package,findings_json=$findings,status=$status,revision=$revision,message_id=$message,updated_at_utc=$at WHERE workspace_id=$w AND render_id=$id AND revision=$expected",
                ("$package", next.Package is null ? DBNull.Value : JsonSerializer.Serialize(next.Package)),
                ("$findings", JsonSerializer.Serialize(next.Findings)), ("$status", EnumText(next.Status)),
                ("$revision", next.Revision), ("$message", next.MessageId!.Value.ToString("D")),
                ("$at", at.ToString("O")), ("$w", workspaceId), ("$id", renderId.ToString("D")),
                ("$expected", expectedRevision));
            if (affected != 1) throw new EpubRenderConflictException("Stale EPUB render revision.");
            sideEffect(connection, tx, next);
            PersistFindings(connection, tx, next, at);
            History(connection, tx, next, operation, actor, reason, at);
            Receipt(connection, tx, workspaceId, operationId, renderId, fingerprint, payload, next, at);
            Outbox(connection, tx, next, $"epub.render.{next.Status.ToString().ToLowerInvariant()}", at);
            tx.Commit();
            return next;
        }
        finally { _gate.Release(); }
    }

    private EpubRenderState? Load(string workspaceId, Guid renderId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM epub_render_history WHERE workspace_id=$w AND render_id=$id ORDER BY revision DESC LIMIT 1";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$id", renderId.ToString("D"));
        var json = command.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<EpubRenderState>(json)
            ?? throw new EpubRenderConflictException("Invalid persisted EPUB state.");
    }

    private (Guid RenderId, string Fingerprint, string Payload, EpubRenderState State)? LoadReceipt(string workspaceId, Guid operationId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT render_id,request_fingerprint,payload_digest,response_json FROM epub_render_receipts WHERE workspace_id=$w AND operation_id=$o";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$o", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var state = JsonSerializer.Deserialize<EpubRenderState>(reader.GetString(3))
            ?? throw new EpubRenderConflictException("Invalid persisted EPUB receipt.");
        return (Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), state);
    }

    private static void RequireReplay((Guid RenderId, string Fingerprint, string Payload, EpubRenderState State) replay,
        Guid renderId, string fingerprint, string payload)
    {
        if (replay.RenderId != renderId || !StringComparer.Ordinal.Equals(replay.Fingerprint, fingerprint)
            || !StringComparer.Ordinal.Equals(replay.Payload, payload))
            throw new EpubRenderConflictException("Operation reused with a different payload.");
    }

    private static void ValidatePackage(EpubPackage package)
    {
        if (string.IsNullOrWhiteSpace(package.PackageDigest) || package.Entries.Count == 0)
            throw new EpubRenderValidationException("Materialized EPUB package is required.");
        var ordered = package.Entries.OrderBy(x => x.Order).ToArray();
        if (ordered[0].Path != "mimetype" || ordered[0].Compression != EpubCompression.Stored ||
            ordered.Select(x => x.Order).Distinct().Count() != ordered.Length ||
            ordered.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new EpubRenderValidationException("EPUB package ordering or identity is invalid.");
    }

    private static void PersistEntries(SqliteConnection connection, SqliteTransaction tx, EpubRenderState state)
    {
        foreach (var entry in state.Package!.Entries.OrderBy(x => x.Order))
            Exec(connection, tx,
                "INSERT INTO epub_render_entries(workspace_id,render_id,entry_path,media_type,content_digest,length,compression,entry_order) VALUES($w,$r,$p,$m,$d,$l,$c,$o)",
                ("$w", state.WorkspaceId), ("$r", state.RenderId.ToString("D")), ("$p", entry.Path),
                ("$m", entry.MediaType), ("$d", entry.ContentDigest), ("$l", entry.Length),
                ("$c", EnumText(entry.Compression)), ("$o", entry.Order));
    }

    private static void PersistFindings(SqliteConnection connection, SqliteTransaction tx, EpubRenderState state, DateTimeOffset at)
    {
        foreach (var finding in state.Findings)
            Exec(connection, tx,
                "INSERT OR REPLACE INTO epub_render_findings(workspace_id,render_id,finding_id,code,category,severity,evidence_digest,finding_json,created_at_utc) VALUES($w,$r,$id,$c,$category,$s,$e,$j,$at)",
                ("$w", state.WorkspaceId), ("$r", state.RenderId.ToString("D")),
                ("$id", finding.FindingId.ToString("D")), ("$c", finding.Code),
                ("$category", EnumText(finding.Category)), ("$s", EnumText(finding.Severity)),
                ("$e", finding.EvidenceDigest), ("$j", JsonSerializer.Serialize(finding)), ("$at", at.ToString("O")));
    }

    private static void PersistDecision(SqliteConnection connection, SqliteTransaction tx, EpubDecisionCommand command,
        EpubRenderState state, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO epub_render_decisions(workspace_id,render_id,operation_id,decision,reason,evidence,evidence_digest,actor,revision,occurred_at_utc) VALUES($w,$r,$o,$d,$reason,$e,$ed,$actor,$v,$at)",
        ("$w", command.WorkspaceId), ("$r", command.RenderId.ToString("D")),
        ("$o", command.RequestId.ToString("D")), ("$d", EnumText(command.Decision)),
        ("$reason", command.Reason), ("$e", command.Evidence), ("$ed", command.EvidenceDigest),
        ("$actor", command.Actor), ("$v", state.Revision), ("$at", at.ToString("O")));

    private static void History(SqliteConnection connection, SqliteTransaction tx, EpubRenderState state,
        string operation, string actor, string reason, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO epub_render_history(workspace_id,render_id,revision,operation,actor,reason,snapshot_json,occurred_at_utc) VALUES($w,$r,$v,$o,$actor,$reason,$j,$at)",
        ("$w", state.WorkspaceId), ("$r", state.RenderId.ToString("D")), ("$v", state.Revision),
        ("$o", operation), ("$actor", actor), ("$reason", reason),
        ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Receipt(SqliteConnection connection, SqliteTransaction tx, string workspaceId,
        Guid operationId, Guid renderId, string fingerprint, string payload, EpubRenderState state, DateTimeOffset at) =>
        Exec(connection, tx,
            "INSERT INTO epub_render_receipts(workspace_id,operation_id,render_id,request_fingerprint,payload_digest,response_json,created_at_utc) VALUES($w,$o,$r,$f,$p,$j,$at)",
            ("$w", workspaceId), ("$o", operationId.ToString("D")), ("$r", renderId.ToString("D")),
            ("$f", fingerprint), ("$p", payload), ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Outbox(SqliteConnection connection, SqliteTransaction tx, EpubRenderState state,
        string eventType, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO epub_render_outbox(message_id,workspace_id,render_id,revision,event_type,payload_json,created_at_utc) VALUES($m,$w,$r,$v,$e,$p,$at)",
        ("$m", state.MessageId!.Value.ToString("D")), ("$w", state.WorkspaceId),
        ("$r", state.RenderId.ToString("D")), ("$v", state.Revision), ("$e", eventType),
        ("$p", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static int Exec(SqliteConnection connection, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private static string EnumText<T>(T value) where T : struct, Enum => value.ToString().ToUpperInvariant();
    private static Guid MessageId(Guid renderId, long revision) => DeterministicGuid($"epub-render:{renderId:D}:{revision}");
    private static Guid DeterministicGuid(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public ValueTask DisposeAsync() { _gate.Dispose(); return ValueTask.CompletedTask; }
}

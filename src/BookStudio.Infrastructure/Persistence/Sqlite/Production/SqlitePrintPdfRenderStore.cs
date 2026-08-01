using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Production;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Production;

public sealed class SqlitePrintPdfRenderStore : IPrintPdfRenderStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqlitePrintPdfRenderStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async ValueTask<PrintPdfSubmissionResult> SubmitAsync(
        PrintPdfRenderRequest request, PrintPdfArtifact artifact, DateTimeOffset at, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ValidateArtifact(artifact);
            var payload = Digest(JsonSerializer.Serialize(new { Request = request, Artifact = artifact }));
            var replay = LoadReceipt(request.WorkspaceId, request.RequestId);
            if (replay is not null)
            {
                RequireReplay(replay.Value, request.RenderId, request.RequestFingerprint, payload);
                return new PrintPdfSubmissionResult(replay.Value.State, true);
            }
            if (Load(request.WorkspaceId, request.RenderId) is not null)
                throw new PrintPdfConflictException("Print PDF render already exists.");

            var state = new PrintPdfRenderState(request.RenderId, request.ProjectId, request.WorkspaceId,
                request.Authority, request.Geometry, request.Paper, request.Metadata, artifact, [],
                PrintPdfRenderStatus.Rendered, 1, MessageId(request.RenderId, 1), at, at);

            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            Exec(connection, tx,
                "INSERT INTO print_pdf_renders(workspace_id,render_id,project_id,authority_json,geometry_json,paper_json,metadata_json,artifact_json,findings_json,status,revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,$g,$paper,$m,$artifact,'[]',$s,1,$message,$at,$at)",
                ("$w", request.WorkspaceId), ("$id", request.RenderId.ToString("D")),
                ("$p", request.ProjectId.ToString("D")), ("$a", JsonSerializer.Serialize(request.Authority)),
                ("$g", JsonSerializer.Serialize(request.Geometry)), ("$paper", JsonSerializer.Serialize(request.Paper)),
                ("$m", JsonSerializer.Serialize(request.Metadata)), ("$artifact", JsonSerializer.Serialize(artifact)),
                ("$s", EnumText(state.Status)), ("$message", state.MessageId!.Value.ToString("D")),
                ("$at", at.ToString("O")));
            PersistPages(connection, tx, state);
            PersistResources(connection, tx, request);
            History(connection, tx, state, "SUBMIT", request.Actor, "print PDF rendered and submitted", at);
            Receipt(connection, tx, request.WorkspaceId, request.RequestId, request.RenderId,
                request.RequestFingerprint, payload, state, at);
            Outbox(connection, tx, state, "print.pdf.rendered", at);
            tx.Commit();
            return new PrintPdfSubmissionResult(state, false);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<PrintPdfRenderState> ValidateAsync(
        PrintPdfValidationCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.RenderId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, Digest(JsonSerializer.Serialize(command)), "VALIDATE", command.Actor,
            "print PDF validation recorded", at, state => state with
            {
                Findings = command.ExternalFindings,
                Status = command.ExternalFindings.Any(x => x.Severity == PrintPdfSeverity.Blocking)
                    ? PrintPdfRenderStatus.ReviewRequired : PrintPdfRenderStatus.Validated,
                Revision = state.Revision + 1,
                MessageId = MessageId(state.RenderId, state.Revision + 1),
                UpdatedAtUtc = at
            }, static (_, _, _) => { }, ct);

    public ValueTask<PrintPdfRenderState> DecideAsync(
        PrintPdfDecisionCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.RenderId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, Digest(JsonSerializer.Serialize(command)), "DECIDE", command.Actor,
            command.Reason, at, state => state with
            {
                Status = command.Decision switch
                {
                    PrintPdfDecision.Approve => PrintPdfRenderStatus.Approved,
                    PrintPdfDecision.ReturnToRepair => PrintPdfRenderStatus.RepairRequired,
                    PrintPdfDecision.Reject => PrintPdfRenderStatus.Rejected,
                    PrintPdfDecision.Supersede => PrintPdfRenderStatus.Superseded,
                    _ => throw new PrintPdfValidationException("Unsupported print PDF decision.")
                },
                Revision = state.Revision + 1,
                MessageId = MessageId(state.RenderId, state.Revision + 1),
                UpdatedAtUtc = at
            }, (connection, tx, next) => PersistDecision(connection, tx, command, next, at), ct);

    public ValueTask<PrintPdfRenderState?> GetAsync(string workspaceId, Guid renderId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Load(workspaceId, renderId));
    }

    private async ValueTask<PrintPdfRenderState> Mutate(string workspaceId, Guid renderId, long expectedRevision,
        Guid operationId, string fingerprint, string payload, string operation, string actor, string reason,
        DateTimeOffset at, Func<PrintPdfRenderState, PrintPdfRenderState> mutation,
        Action<SqliteConnection, SqliteTransaction, PrintPdfRenderState> sideEffect, CancellationToken ct)
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
            var current = Load(workspaceId, renderId) ?? throw new PrintPdfValidationException("Print PDF render not found.");
            if (current.Revision != expectedRevision) throw new PrintPdfConflictException("Stale print PDF render revision.");
            var next = mutation(current);
            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            var affected = Exec(connection, tx,
                "UPDATE print_pdf_renders SET artifact_json=$artifact,findings_json=$findings,status=$status,revision=$revision,message_id=$message,updated_at_utc=$at WHERE workspace_id=$w AND render_id=$id AND revision=$expected",
                ("$artifact", next.Artifact is null ? DBNull.Value : JsonSerializer.Serialize(next.Artifact)),
                ("$findings", JsonSerializer.Serialize(next.Findings)), ("$status", EnumText(next.Status)),
                ("$revision", next.Revision), ("$message", next.MessageId!.Value.ToString("D")),
                ("$at", at.ToString("O")), ("$w", workspaceId), ("$id", renderId.ToString("D")),
                ("$expected", expectedRevision));
            if (affected != 1) throw new PrintPdfConflictException("Stale print PDF render revision.");
            sideEffect(connection, tx, next);
            PersistFindings(connection, tx, next, at);
            History(connection, tx, next, operation, actor, reason, at);
            Receipt(connection, tx, workspaceId, operationId, renderId, fingerprint, payload, next, at);
            Outbox(connection, tx, next, $"print.pdf.{next.Status.ToString().ToLowerInvariant()}", at);
            tx.Commit();
            return next;
        }
        finally { _gate.Release(); }
    }

    private PrintPdfRenderState? Load(string workspaceId, Guid renderId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM print_pdf_history WHERE workspace_id=$w AND render_id=$id ORDER BY revision DESC LIMIT 1";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$id", renderId.ToString("D"));
        var json = command.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<PrintPdfRenderState>(json)
            ?? throw new PrintPdfConflictException("Invalid persisted print PDF state.");
    }

    private (Guid RenderId, string Fingerprint, string Payload, PrintPdfRenderState State)? LoadReceipt(string workspaceId, Guid operationId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT render_id,request_fingerprint,payload_digest,response_json FROM print_pdf_receipts WHERE workspace_id=$w AND operation_id=$o";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$o", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var state = JsonSerializer.Deserialize<PrintPdfRenderState>(reader.GetString(3))
            ?? throw new PrintPdfConflictException("Invalid persisted print PDF receipt.");
        return (Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), state);
    }

    private static void RequireReplay((Guid RenderId, string Fingerprint, string Payload, PrintPdfRenderState State) replay,
        Guid renderId, string fingerprint, string payload)
    {
        if (replay.RenderId != renderId || !StringComparer.Ordinal.Equals(replay.Fingerprint, fingerprint)
            || !StringComparer.Ordinal.Equals(replay.Payload, payload))
            throw new PrintPdfConflictException("Operation reused with a different payload.");
    }

    private static void ValidateArtifact(PrintPdfArtifact artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.ArtifactDigest) || artifact.Pages.Count == 0 ||
            artifact.Pages.Select(x => x.PageNumber).Distinct().Count() != artifact.Pages.Count)
            throw new PrintPdfValidationException("Materialized print PDF artifact is invalid.");
    }

    private static void PersistPages(SqliteConnection connection, SqliteTransaction tx, PrintPdfRenderState state)
    {
        foreach (var page in state.Artifact!.Pages.OrderBy(x => x.PageNumber))
            Exec(connection, tx,
                "INSERT INTO print_pdf_pages(workspace_id,render_id,page_id,page_number,page_kind,page_side,content_digest,boxes_json) VALUES($w,$r,$id,$n,$k,$s,$d,$b)",
                ("$w", state.WorkspaceId), ("$r", state.RenderId.ToString("D")),
                ("$id", page.PageId.ToString("D")), ("$n", page.PageNumber), ("$k", EnumText(page.Kind)),
                ("$s", EnumText(page.Side)), ("$d", page.ContentDigest), ("$b", JsonSerializer.Serialize(page.Boxes)));
    }

    private static void PersistResources(SqliteConnection connection, SqliteTransaction tx, PrintPdfRenderRequest request)
    {
        foreach (var font in request.Fonts)
            Exec(connection, tx,
                "INSERT INTO print_pdf_resources(workspace_id,render_id,resource_id,resource_kind,content_digest,resource_json) VALUES($w,$r,$id,'FONT',$d,$j)",
                ("$w", request.WorkspaceId), ("$r", request.RenderId.ToString("D")),
                ("$id", font.FontId.ToString("D")), ("$d", font.ContentDigest), ("$j", JsonSerializer.Serialize(font)));
        foreach (var image in request.Images)
            Exec(connection, tx,
                "INSERT INTO print_pdf_resources(workspace_id,render_id,resource_id,resource_kind,content_digest,resource_json) VALUES($w,$r,$id,'IMAGE',$d,$j)",
                ("$w", request.WorkspaceId), ("$r", request.RenderId.ToString("D")),
                ("$id", image.ImageId.ToString("D")), ("$d", image.ContentDigest), ("$j", JsonSerializer.Serialize(image)));
    }

    private static void PersistFindings(SqliteConnection connection, SqliteTransaction tx, PrintPdfRenderState state, DateTimeOffset at)
    {
        foreach (var finding in state.Findings)
            Exec(connection, tx,
                "INSERT OR REPLACE INTO print_pdf_findings(workspace_id,render_id,finding_id,code,category,severity,evidence_digest,finding_json,created_at_utc) VALUES($w,$r,$id,$c,$category,$s,$e,$j,$at)",
                ("$w", state.WorkspaceId), ("$r", state.RenderId.ToString("D")),
                ("$id", finding.FindingId.ToString("D")), ("$c", finding.Code),
                ("$category", EnumText(finding.Category)), ("$s", EnumText(finding.Severity)),
                ("$e", finding.EvidenceDigest), ("$j", JsonSerializer.Serialize(finding)), ("$at", at.ToString("O")));
    }

    private static void PersistDecision(SqliteConnection connection, SqliteTransaction tx, PrintPdfDecisionCommand command,
        PrintPdfRenderState state, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO print_pdf_decisions(workspace_id,render_id,operation_id,decision,reason,evidence,evidence_digest,actor,revision,occurred_at_utc) VALUES($w,$r,$o,$d,$reason,$e,$ed,$actor,$v,$at)",
        ("$w", command.WorkspaceId), ("$r", command.RenderId.ToString("D")),
        ("$o", command.RequestId.ToString("D")), ("$d", EnumText(command.Decision)),
        ("$reason", command.Reason), ("$e", command.Evidence), ("$ed", command.EvidenceDigest),
        ("$actor", command.Actor), ("$v", state.Revision), ("$at", at.ToString("O")));

    private static void History(SqliteConnection connection, SqliteTransaction tx, PrintPdfRenderState state,
        string operation, string actor, string reason, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO print_pdf_history(workspace_id,render_id,revision,operation,actor,reason,snapshot_json,occurred_at_utc) VALUES($w,$r,$v,$o,$actor,$reason,$j,$at)",
        ("$w", state.WorkspaceId), ("$r", state.RenderId.ToString("D")), ("$v", state.Revision),
        ("$o", operation), ("$actor", actor), ("$reason", reason),
        ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Receipt(SqliteConnection connection, SqliteTransaction tx, string workspaceId,
        Guid operationId, Guid renderId, string fingerprint, string payload, PrintPdfRenderState state, DateTimeOffset at) =>
        Exec(connection, tx,
            "INSERT INTO print_pdf_receipts(workspace_id,operation_id,render_id,request_fingerprint,payload_digest,response_json,created_at_utc) VALUES($w,$o,$r,$f,$p,$j,$at)",
            ("$w", workspaceId), ("$o", operationId.ToString("D")), ("$r", renderId.ToString("D")),
            ("$f", fingerprint), ("$p", payload), ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Outbox(SqliteConnection connection, SqliteTransaction tx, PrintPdfRenderState state,
        string eventType, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO print_pdf_outbox(message_id,workspace_id,render_id,revision,event_type,payload_json,created_at_utc) VALUES($m,$w,$r,$v,$e,$p,$at)",
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
    private static Guid MessageId(Guid renderId, long revision) => DeterministicGuid($"print-pdf:{renderId:D}:{revision}");
    private static Guid DeterministicGuid(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public ValueTask DisposeAsync() { _gate.Dispose(); return ValueTask.CompletedTask; }
}

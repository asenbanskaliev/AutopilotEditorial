using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Production;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Production;

public sealed class SqliteDocxRenderStore : IDocxRenderStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteDocxRenderStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async ValueTask<DocxSubmissionResult> SubmitAsync(DocxRenderRequest request, DocxArtifact artifact, DateTimeOffset at, CancellationToken ct = default)
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
                return new DocxSubmissionResult(replay.Value.State, true);
            }
            if (Load(request.WorkspaceId, request.RenderId) is not null)
                throw new DocxConflictException("DOCX render already exists.");

            var state = new DocxRenderState(request.RenderId, request.ProjectId, request.WorkspaceId,
                request.Authority, request.Locale, request.TemplateProfile, request.CompatibilityTarget,
                artifact, [], DocxRenderStatus.Rendered, 1, MessageId(request.RenderId, 1), at, at);

            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            Exec(connection, tx,
                "INSERT INTO docx_renders(workspace_id,render_id,project_id,authority_json,locale,template_profile,compatibility_target,artifact_json,findings_json,status,revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,$l,$t,$c,$artifact,'[]',$s,1,$m,$at,$at)",
                ("$w", request.WorkspaceId), ("$id", request.RenderId.ToString("D")), ("$p", request.ProjectId.ToString("D")),
                ("$a", JsonSerializer.Serialize(request.Authority)), ("$l", request.Locale), ("$t", request.TemplateProfile),
                ("$c", request.CompatibilityTarget), ("$artifact", JsonSerializer.Serialize(artifact)),
                ("$s", EnumText(state.Status)), ("$m", state.MessageId!.Value.ToString("D")), ("$at", at.ToString("O")));
            PersistArtifact(connection, tx, request, artifact);
            History(connection, tx, state, "SUBMIT", request.Actor, "DOCX rendered and submitted", at);
            Receipt(connection, tx, request.WorkspaceId, request.RequestId, request.RenderId, request.RequestFingerprint, payload, state, at);
            Outbox(connection, tx, state, "docx.rendered", at);
            tx.Commit();
            return new DocxSubmissionResult(state, false);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<DocxRenderState> ValidateAsync(DocxValidationCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.RenderId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, Digest(JsonSerializer.Serialize(command)), "VALIDATE", command.Actor,
            "DOCX validation recorded", at, state => state with
            {
                Findings = command.ExternalFindings,
                Status = command.ExternalFindings.Any(x => x.Severity == DocxSeverity.Blocking)
                    ? DocxRenderStatus.ReviewRequired : DocxRenderStatus.Validated,
                Revision = state.Revision + 1,
                MessageId = MessageId(state.RenderId, state.Revision + 1),
                UpdatedAtUtc = at
            }, static (_, _, _) => { }, ct);

    public ValueTask<DocxRenderState> DecideAsync(DocxDecisionCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.WorkspaceId, command.RenderId, command.ExpectedRevision, command.RequestId,
            command.RequestFingerprint, Digest(JsonSerializer.Serialize(command)), "DECIDE", command.Actor,
            command.Reason, at, state => state with
            {
                Status = command.Decision switch
                {
                    DocxDecision.Approve => DocxRenderStatus.Approved,
                    DocxDecision.ReturnToRepair => DocxRenderStatus.RepairRequired,
                    DocxDecision.Reject => DocxRenderStatus.Rejected,
                    DocxDecision.Supersede => DocxRenderStatus.Superseded,
                    _ => throw new DocxValidationException("Unsupported DOCX decision.")
                },
                Revision = state.Revision + 1,
                MessageId = MessageId(state.RenderId, state.Revision + 1),
                UpdatedAtUtc = at
            }, (connection, tx, next) => PersistDecision(connection, tx, command, next, at), ct);

    public ValueTask<DocxRenderState?> GetAsync(string workspaceId, Guid renderId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Load(workspaceId, renderId));
    }

    private async ValueTask<DocxRenderState> Mutate(string workspaceId, Guid renderId, long expectedRevision,
        Guid operationId, string fingerprint, string payload, string operation, string actor, string reason,
        DateTimeOffset at, Func<DocxRenderState, DocxRenderState> mutation,
        Action<SqliteConnection, SqliteTransaction, DocxRenderState> sideEffect, CancellationToken ct)
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
            var current = Load(workspaceId, renderId) ?? throw new DocxValidationException("DOCX render not found.");
            if (current.Revision != expectedRevision) throw new DocxConflictException("Stale DOCX render revision.");
            var next = mutation(current);
            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            var affected = Exec(connection, tx,
                "UPDATE docx_renders SET artifact_json=$artifact,findings_json=$findings,status=$status,revision=$revision,message_id=$message,updated_at_utc=$at WHERE workspace_id=$w AND render_id=$id AND revision=$expected",
                ("$artifact", next.Artifact is null ? DBNull.Value : JsonSerializer.Serialize(next.Artifact)),
                ("$findings", JsonSerializer.Serialize(next.Findings)), ("$status", EnumText(next.Status)),
                ("$revision", next.Revision), ("$message", next.MessageId!.Value.ToString("D")), ("$at", at.ToString("O")),
                ("$w", workspaceId), ("$id", renderId.ToString("D")), ("$expected", expectedRevision));
            if (affected != 1) throw new DocxConflictException("Stale DOCX render revision.");
            sideEffect(connection, tx, next);
            PersistFindings(connection, tx, next, at);
            History(connection, tx, next, operation, actor, reason, at);
            Receipt(connection, tx, workspaceId, operationId, renderId, fingerprint, payload, next, at);
            Outbox(connection, tx, next, $"docx.{next.Status.ToString().ToLowerInvariant()}", at);
            tx.Commit();
            return next;
        }
        finally { _gate.Release(); }
    }

    private DocxRenderState? Load(string workspaceId, Guid renderId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM docx_render_history WHERE workspace_id=$w AND render_id=$id ORDER BY revision DESC LIMIT 1";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$id", renderId.ToString("D"));
        var json = command.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<DocxRenderState>(json)
            ?? throw new DocxConflictException("Invalid persisted DOCX state.");
    }

    private (Guid RenderId, string Fingerprint, string Payload, DocxRenderState State)? LoadReceipt(string workspaceId, Guid operationId)
    {
        using var connection = _factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT render_id,request_fingerprint,payload_digest,response_json FROM docx_render_receipts WHERE workspace_id=$w AND operation_id=$o";
        command.Parameters.AddWithValue("$w", workspaceId);
        command.Parameters.AddWithValue("$o", operationId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var state = JsonSerializer.Deserialize<DocxRenderState>(reader.GetString(3))
            ?? throw new DocxConflictException("Invalid persisted DOCX receipt.");
        return (Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), state);
    }

    private static void RequireReplay((Guid RenderId, string Fingerprint, string Payload, DocxRenderState State) replay,
        Guid renderId, string fingerprint, string payload)
    {
        if (replay.RenderId != renderId || !StringComparer.Ordinal.Equals(replay.Fingerprint, fingerprint)
            || !StringComparer.Ordinal.Equals(replay.Payload, payload))
            throw new DocxConflictException("Operation reused with a different payload.");
    }

    private static void ValidateArtifact(DocxArtifact artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.ArtifactDigest) || artifact.Parts.Count == 0 ||
            artifact.Parts.Select(x => x.Order).Distinct().Count() != artifact.Parts.Count ||
            artifact.Relationships.Any(x => x.External))
            throw new DocxValidationException("Materialized DOCX artifact is invalid.");
    }

    private static void PersistArtifact(SqliteConnection connection, SqliteTransaction tx, DocxRenderRequest request, DocxArtifact artifact)
    {
        foreach (var part in artifact.Parts.OrderBy(x => x.Order))
            Exec(connection, tx,
                "INSERT INTO docx_render_parts(workspace_id,render_id,part_name,part_order,content_type,content_digest) VALUES($w,$r,$n,$o,$t,$d)",
                ("$w", request.WorkspaceId), ("$r", request.RenderId.ToString("D")), ("$n", part.PartName),
                ("$o", part.Order), ("$t", part.ContentType), ("$d", part.ContentDigest));
        foreach (var relationship in artifact.Relationships.OrderBy(x => x.RelationshipId, StringComparer.Ordinal))
            Exec(connection, tx,
                "INSERT INTO docx_render_relationships(workspace_id,render_id,relationship_id,source_part,target,relationship_type,external) VALUES($w,$r,$id,$s,$t,$type,$e)",
                ("$w", request.WorkspaceId), ("$r", request.RenderId.ToString("D")), ("$id", relationship.RelationshipId),
                ("$s", relationship.SourcePart), ("$t", relationship.Target), ("$type", relationship.Type), ("$e", relationship.External ? 1 : 0));
        foreach (var resource in request.Resources.OrderBy(x => x.PartName, StringComparer.Ordinal).ThenBy(x => x.ResourceId))
            Exec(connection, tx,
                "INSERT INTO docx_render_resources(workspace_id,render_id,resource_id,part_name,content_digest,rights_approved,accessibility_alternative) VALUES($w,$r,$id,$p,$d,$rights,$alt)",
                ("$w", request.WorkspaceId), ("$r", request.RenderId.ToString("D")), ("$id", resource.ResourceId.ToString("D")),
                ("$p", resource.PartName), ("$d", resource.ContentDigest), ("$rights", resource.RightsApproved ? 1 : 0),
                ("$alt", resource.AccessibilityAlternative));
    }

    private static void PersistFindings(SqliteConnection connection, SqliteTransaction tx, DocxRenderState state, DateTimeOffset at)
    {
        foreach (var finding in state.Findings)
            Exec(connection, tx,
                "INSERT OR REPLACE INTO docx_render_findings(workspace_id,render_id,finding_id,code,severity,finding_json,evidence_digest,created_at_utc) VALUES($w,$r,$id,$c,$s,$j,$e,$at)",
                ("$w", state.WorkspaceId), ("$r", state.RenderId.ToString("D")), ("$id", finding.FindingId.ToString("D")),
                ("$c", finding.Code), ("$s", EnumText(finding.Severity)), ("$j", JsonSerializer.Serialize(finding)),
                ("$e", finding.EvidenceDigest), ("$at", at.ToString("O")));
    }

    private static void PersistDecision(SqliteConnection connection, SqliteTransaction tx, DocxDecisionCommand command,
        DocxRenderState state, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO docx_render_decisions(workspace_id,render_id,operation_id,decision,reason,evidence,evidence_digest,actor,revision,occurred_at_utc) VALUES($w,$r,$o,$d,$reason,$e,$ed,$actor,$v,$at)",
        ("$w", command.WorkspaceId), ("$r", command.RenderId.ToString("D")), ("$o", command.RequestId.ToString("D")),
        ("$d", EnumText(command.Decision)), ("$reason", command.Reason), ("$e", command.Evidence),
        ("$ed", command.EvidenceDigest), ("$actor", command.Actor), ("$v", state.Revision), ("$at", at.ToString("O")));

    private static void History(SqliteConnection connection, SqliteTransaction tx, DocxRenderState state,
        string operation, string actor, string reason, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO docx_render_history(workspace_id,render_id,revision,operation,actor,reason,snapshot_json,occurred_at_utc) VALUES($w,$r,$v,$o,$actor,$reason,$j,$at)",
        ("$w", state.WorkspaceId), ("$r", state.RenderId.ToString("D")), ("$v", state.Revision), ("$o", operation),
        ("$actor", actor), ("$reason", reason), ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Receipt(SqliteConnection connection, SqliteTransaction tx, string workspaceId,
        Guid operationId, Guid renderId, string fingerprint, string payload, DocxRenderState state, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO docx_render_receipts(workspace_id,operation_id,render_id,request_fingerprint,payload_digest,response_json,created_at_utc) VALUES($w,$o,$r,$f,$p,$j,$at)",
        ("$w", workspaceId), ("$o", operationId.ToString("D")), ("$r", renderId.ToString("D")),
        ("$f", fingerprint), ("$p", payload), ("$j", JsonSerializer.Serialize(state)), ("$at", at.ToString("O")));

    private static void Outbox(SqliteConnection connection, SqliteTransaction tx, DocxRenderState state,
        string eventType, DateTimeOffset at) => Exec(connection, tx,
        "INSERT INTO docx_render_outbox(message_id,workspace_id,render_id,revision,event_type,payload_json,created_at_utc) VALUES($m,$w,$r,$v,$e,$p,$at)",
        ("$m", state.MessageId!.Value.ToString("D")), ("$w", state.WorkspaceId), ("$r", state.RenderId.ToString("D")),
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
    private static Guid MessageId(Guid renderId, long revision) => DeterministicGuid($"docx:{renderId:D}:{revision}");
    private static Guid DeterministicGuid(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

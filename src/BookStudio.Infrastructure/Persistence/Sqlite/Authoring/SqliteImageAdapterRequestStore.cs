using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteImageAdapterRequestStore : IImageAdapterRequestStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteImageAdapterRequestStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async ValueTask<ImageAdapterSubmissionResult> SubmitAsync(ImageAdapterRequest request, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(request);
        await _gate.WaitAsync(ct);
        try
        {
            var payload = Hash(JsonSerializer.Serialize(request));
            var existing = Load(request.WorkspaceId, request.RequestId);
            if (existing is not null)
            {
                RequireReceipt(request.WorkspaceId, request.RequestId, request.RequestId, request.RequestFingerprint, payload);
                return new(existing, true);
            }

            RequireBriefAuthority(request.WorkspaceId, request.ProjectId, request.VisualBriefId,
                request.ExpectedVisualBriefRevision, request.ExpectedVisualBriefDigest);
            var message = MessageId(request.RequestId, 1);
            var state = new ImageAdapterRequestState(request.RequestId, request.ProjectId, request.WorkspaceId,
                request.VisualBriefId, request.ExpectedVisualBriefRevision, request.ExpectedVisualBriefDigest,
                request.AssetType, request.AdapterId, request.AdapterVersion, request.AdapterKind, request.Operation,
                request.RequiredCapabilities, Hash(request.Prompt), request.GenerationParametersJson,
                request.OutputPolicy, request.RetryPolicy, [], [], ImageAdapterRequestStatus.Submitted,
                null, 1, message, at, at);

            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            Exec(connection, tx, "INSERT INTO image_adapter_requests(workspace_id,request_id,project_id,visual_brief_id,expected_visual_brief_revision,expected_visual_brief_digest,asset_type,adapter_id,adapter_version,adapter_kind,operation_mode,required_capabilities_json,prompt_digest,generation_parameters_json,output_policy_json,retry_policy_json,status,last_error_json,request_fingerprint,revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$b,$br,$bd,$t,$a,$av,$ak,$o,$c,$pd,$g,$op,$rp,'SUBMITTED',NULL,$f,1,$m,$at,$at)",
                ("$w",request.WorkspaceId),("$id",request.RequestId.ToString("D")),("$p",request.ProjectId.ToString("D")),
                ("$b",request.VisualBriefId.ToString("D")),("$br",request.ExpectedVisualBriefRevision),("$bd",request.ExpectedVisualBriefDigest),
                ("$t",EnumText(request.AssetType)),("$a",request.AdapterId),("$av",request.AdapterVersion),("$ak",EnumText(request.AdapterKind)),
                ("$o",EnumText(request.Operation)),("$c",JsonSerializer.Serialize(request.RequiredCapabilities)),("$pd",state.PromptDigest),
                ("$g",request.GenerationParametersJson),("$op",JsonSerializer.Serialize(request.OutputPolicy)),("$rp",JsonSerializer.Serialize(request.RetryPolicy)),
                ("$f",request.RequestFingerprint),("$m",message.ToString("D")),("$at",at.ToString("O")));
            History(connection, tx, state, "SUBMIT", request.Actor, "request submitted", at);
            Receipt(connection, tx, request.WorkspaceId, request.RequestId, request.RequestId, request.RequestFingerprint, payload, state, at);
            Outbox(connection, tx, state, "image.adapter.submitted", at);
            tx.Commit();
            return new(state, false);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<ImageAdapterRequestState> RecordAttemptAsync(ImageAdapterAttempt attempt, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(attempt.WorkspaceId, attempt.RequestId, attempt.ExpectedRevision,
            DeterministicGuid(attempt.RequestId, $"attempt:{attempt.AttemptId:D}"), attempt.RequestFingerprint,
            Hash(JsonSerializer.Serialize(attempt)), "ATTEMPT", attempt.Actor, "adapter attempt recorded", at, state =>
            {
                if (state.Status is ImageAdapterRequestStatus.Completed or ImageAdapterRequestStatus.Failed or ImageAdapterRequestStatus.Cancelled)
                    throw new ImageAdapterTransitionException("Terminal requests cannot accept attempts.");
                if (attempt.AttemptNumber != state.Attempts.Count + 1)
                    throw new ImageAdapterConflictException("Attempt number is not sequential.");
                var status = attempt.Result.Succeeded ? ImageAdapterRequestStatus.Running :
                    attempt.Result.Error?.Retryable == true && attempt.AttemptNumber < state.RetryPolicy.MaximumAttempts
                        ? ImageAdapterRequestStatus.RetryPending : ImageAdapterRequestStatus.Failed;
                return state with { Attempts = state.Attempts.Append(attempt).ToArray(), Status = status,
                    LastError = attempt.Result.Error, Revision = state.Revision + 1,
                    MessageId = MessageId(state.RequestId, state.Revision + 1), UpdatedAtUtc = at };
            }, (connection, tx, next) => PersistAttempt(connection, tx, attempt, at), ct);

    public ValueTask<ImageAdapterRequestState> CompleteAsync(ImageAdapterCompletion completion, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(completion.WorkspaceId, completion.RequestId, completion.ExpectedRevision,
            DeterministicGuid(completion.RequestId, $"complete:{completion.AttemptId:D}"), completion.RequestFingerprint,
            Hash(JsonSerializer.Serialize(completion)), "COMPLETE", completion.Actor, "outputs registered", at, state =>
            {
                if (state.Status != ImageAdapterRequestStatus.Running)
                    throw new ImageAdapterTransitionException("Only running requests can complete.");
                if (completion.Outputs.Count == 0) throw new ImageAdapterValidationException("Completion requires registered outputs.");
                return state with { Outputs = completion.Outputs.ToArray(), Status = ImageAdapterRequestStatus.Completed,
                    LastError = null, Revision = state.Revision + 1, MessageId = MessageId(state.RequestId, state.Revision + 1), UpdatedAtUtc = at };
            }, (connection, tx, next) =>
            {
                foreach (var output in completion.Outputs)
                {
                    var affected = Exec(connection, tx, "UPDATE image_adapter_outputs SET asset_id=$a,asset_revision=$r,asset_outbox_message_id=$m WHERE workspace_id=$w AND request_id=$id AND output_id=$o AND content_digest=$d",
                        ("$a",output.AssetId.ToString("D")),("$r",output.AssetRevision),("$m",output.OutboxMessageId is null ? DBNull.Value : output.OutboxMessageId.Value.ToString("D")),
                        ("$w",completion.WorkspaceId),("$id",completion.RequestId.ToString("D")),("$o",output.OutputId.ToString("D")),("$d",output.ContentDigest));
                    if (affected != 1) throw new ImageAdapterConflictException("Registered output does not match a persisted adapter output.");
                }
            }, ct);

    public ValueTask<ImageAdapterRequestState> FailAsync(ImageAdapterFailure failure, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(failure.WorkspaceId, failure.RequestId, failure.ExpectedRevision,
            DeterministicGuid(failure.RequestId, $"fail:{failure.RequestFingerprint}"), failure.RequestFingerprint,
            Hash(JsonSerializer.Serialize(failure)), "FAIL", failure.Actor, failure.Error.Message, at, state =>
                state.Status is ImageAdapterRequestStatus.Completed or ImageAdapterRequestStatus.Cancelled
                    ? throw new ImageAdapterTransitionException("Request cannot fail from its current state.")
                    : state with { Status = ImageAdapterRequestStatus.Failed, LastError = failure.Error,
                        Revision = state.Revision + 1, MessageId = MessageId(state.RequestId, state.Revision + 1), UpdatedAtUtc = at }, null, ct);

    public ValueTask<ImageAdapterRequestState> CancelAsync(ImageAdapterCancellation cancellation, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(cancellation.WorkspaceId, cancellation.RequestId, cancellation.ExpectedRevision,
            DeterministicGuid(cancellation.RequestId, $"cancel:{cancellation.RequestFingerprint}"), cancellation.RequestFingerprint,
            Hash(JsonSerializer.Serialize(cancellation)), "CANCEL", cancellation.Actor, cancellation.Reason, at, state =>
                state.Status is ImageAdapterRequestStatus.Completed or ImageAdapterRequestStatus.Failed or ImageAdapterRequestStatus.Cancelled
                    ? throw new ImageAdapterTransitionException("Terminal request cannot be cancelled.")
                    : state with { Status = ImageAdapterRequestStatus.Cancelled,
                        LastError = new ImageAdapterError("CANCELLED", cancellation.Reason, ImageFailureKind.Cancelled, false, cancellation.Reason, Hash(cancellation.Reason)),
                        Revision = state.Revision + 1, MessageId = MessageId(state.RequestId, state.Revision + 1), UpdatedAtUtc = at }, null, ct);

    public ValueTask<ImageAdapterRequestState?> GetAsync(string workspaceId, Guid requestId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Load(workspaceId, requestId));
    }

    private async ValueTask<ImageAdapterRequestState> Mutate(string workspaceId, Guid requestId, long expectedRevision,
        Guid operationId, string fingerprint, string payload, string operation, string actor, string reason, DateTimeOffset at,
        Func<ImageAdapterRequestState, ImageAdapterRequestState> mutation,
        Action<SqliteConnection, SqliteTransaction, ImageAdapterRequestState>? sideEffect, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var replay = LoadReceipt(workspaceId, operationId);
            if (replay is not null)
            {
                if (!StringComparer.Ordinal.Equals(replay.Value.Fingerprint, fingerprint) || !StringComparer.Ordinal.Equals(replay.Value.Payload, payload))
                    throw new ImageAdapterConflictException("Operation reused with a different payload.");
                return replay.Value.State;
            }
            var state = Load(workspaceId, requestId) ?? throw new ImageAdapterValidationException("Image adapter request not found.");
            if (state.Revision != expectedRevision) throw new ImageAdapterConflictException("Stale revision.");
            RequireBriefAuthority(state.WorkspaceId, state.ProjectId, state.VisualBriefId, state.ExpectedVisualBriefRevision, state.ExpectedVisualBriefDigest);
            var next = mutation(state);
            using var connection = _factory.OpenConnection();
            using var tx = connection.BeginTransaction();
            var affected = Exec(connection, tx, "UPDATE image_adapter_requests SET status=$s,last_error_json=$e,revision=$r,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND request_id=$id AND revision=$expected",
                ("$s",EnumText(next.Status)),("$e",next.LastError is null ? DBNull.Value : JsonSerializer.Serialize(next.LastError)),("$r",next.Revision),
                ("$m",next.MessageId!.Value.ToString("D")),("$at",at.ToString("O")),("$w",workspaceId),("$id",requestId.ToString("D")),("$expected",expectedRevision));
            if (affected != 1) throw new ImageAdapterConflictException("Stale revision.");
            sideEffect?.Invoke(connection, tx, next);
            History(connection, tx, next, operation, actor, reason, at);
            Receipt(connection, tx, workspaceId, requestId, operationId, fingerprint, payload, next, at);
            Outbox(connection, tx, next, $"image.adapter.{next.Status.ToString().ToLowerInvariant()}", at);
            tx.Commit();
            return next;
        }
        finally { _gate.Release(); }
    }

    private ImageAdapterRequestState? Load(string workspaceId, Guid requestId)
    {
        using var connection = _factory.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT snapshot_json FROM image_adapter_history WHERE workspace_id=$w AND request_id=$id ORDER BY revision DESC,occurred_at_utc DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$w", workspaceId); cmd.Parameters.AddWithValue("$id", requestId.ToString("D"));
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<ImageAdapterRequestState>(json)
            ?? throw new ImageAdapterConflictException("Invalid persisted image adapter state.");
    }

    private (string Fingerprint, string Payload, ImageAdapterRequestState State)? LoadReceipt(string workspaceId, Guid operationId)
    {
        using var connection = _factory.OpenConnection(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT request_fingerprint,payload_digest,response_json FROM image_adapter_receipts WHERE workspace_id=$w AND operation_id=$o";
        cmd.Parameters.AddWithValue("$w", workspaceId); cmd.Parameters.AddWithValue("$o", operationId.ToString("D"));
        using var reader = cmd.ExecuteReader(); if (!reader.Read()) return null;
        var state = JsonSerializer.Deserialize<ImageAdapterRequestState>(reader.GetString(2))
            ?? throw new ImageAdapterConflictException("Invalid persisted receipt.");
        return (reader.GetString(0), reader.GetString(1), state);
    }

    private void RequireReceipt(string workspaceId, Guid requestId, Guid operationId, string fingerprint, string payload)
    {
        var receipt = LoadReceipt(workspaceId, operationId);
        if (receipt is null || receipt.Value.State.RequestId != requestId ||
            !StringComparer.Ordinal.Equals(receipt.Value.Fingerprint, fingerprint) || !StringComparer.Ordinal.Equals(receipt.Value.Payload, payload))
            throw new ImageAdapterConflictException("Request reused with a different payload.");
    }

    private void RequireBriefAuthority(string workspaceId, Guid projectId, Guid briefId, long revision, string digest)
    {
        using var connection = _factory.OpenConnection(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT project_id,revision,status FROM visual_briefs WHERE workspace_id=$w AND brief_id=$id";
        cmd.Parameters.AddWithValue("$w", workspaceId); cmd.Parameters.AddWithValue("$id", briefId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new ImageAdapterValidationException("Approved visual brief authority not found.");
        var status = reader.GetString(2); var actual = Hash($"{workspaceId}:{briefId:D}:{reader.GetInt64(1)}:{status}");
        if (Guid.Parse(reader.GetString(0)) != projectId || reader.GetInt64(1) != revision || status != "APPROVED" || !StringComparer.Ordinal.Equals(actual, digest))
            throw new ImageAdapterValidationException("Visual brief authority is not exact, current and approved.");
    }

    private static void PersistAttempt(SqliteConnection connection, SqliteTransaction tx, ImageAdapterAttempt attempt, DateTimeOffset at)
    {
        Exec(connection, tx, "INSERT INTO image_adapter_attempts(workspace_id,request_id,attempt_id,attempt_number,adapter_id,adapter_version,result_json,provider_evidence_json,provider_evidence_digest,actor,request_fingerprint,started_at_utc,completed_at_utc) VALUES($w,$id,$a,$n,$ai,$av,$r,$pe,$pd,$actor,$f,$s,$c)",
            ("$w",attempt.WorkspaceId),("$id",attempt.RequestId.ToString("D")),("$a",attempt.AttemptId.ToString("D")),("$n",attempt.AttemptNumber),
            ("$ai",attempt.AdapterId),("$av",attempt.AdapterVersion),("$r",JsonSerializer.Serialize(attempt.Result)),
            ("$pe",attempt.Result.ProviderEvidenceJson),("$pd",attempt.Result.ProviderEvidenceDigest),("$actor",attempt.Actor),("$f",attempt.RequestFingerprint),
            ("$s",at.ToString("O")),("$c",attempt.Result.CompletedAtUtc.ToString("O")));
        foreach (var output in attempt.Result.Outputs)
        {
            ValidateOutput(output);
            var canonical = Path.GetFullPath(Path.Combine(output.StorageRoot, output.RelativePath)).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
            Exec(connection, tx, "INSERT INTO image_adapter_outputs(workspace_id,request_id,output_id,attempt_id,storage_root,relative_path,canonical_storage_identity,media_format,width,height,bytes,color_profile,content_digest,technical_metadata_json,provenance_json,relationships_json,asset_id,asset_revision,asset_outbox_message_id,created_at_utc) VALUES($w,$id,$o,$a,$sr,$rp,$cs,$mf,$wi,$he,$b,$cp,$d,$tm,$p,$r,NULL,NULL,NULL,$at)",
                ("$w",attempt.WorkspaceId),("$id",attempt.RequestId.ToString("D")),("$o",output.OutputId.ToString("D")),("$a",attempt.AttemptId.ToString("D")),
                ("$sr",output.StorageRoot),("$rp",output.RelativePath),("$cs",canonical),("$mf",output.MediaFormat),("$wi",output.Width),("$he",output.Height),
                ("$b",output.Bytes),("$cp",output.ColorProfile),("$d",output.ContentDigest),("$tm",output.TechnicalMetadataJson),
                ("$p",JsonSerializer.Serialize(output.Provenance)),("$r",JsonSerializer.Serialize(output.Relationships)),("$at",at.ToString("O")));
        }
    }

    private static void ValidateRequest(ImageAdapterRequest request)
    {
        if (request.RequestId == Guid.Empty || request.ProjectId == Guid.Empty || request.VisualBriefId == Guid.Empty ||
            request.ExpectedVisualBriefRevision < 1 || request.RequiredCapabilities.Count == 0 || request.RetryPolicy.MaximumAttempts < 1 ||
            string.IsNullOrWhiteSpace(request.WorkspaceId) || string.IsNullOrWhiteSpace(request.ExpectedVisualBriefDigest) ||
            string.IsNullOrWhiteSpace(request.AdapterId) || string.IsNullOrWhiteSpace(request.AdapterVersion) ||
            string.IsNullOrWhiteSpace(request.Prompt) || string.IsNullOrWhiteSpace(request.Actor) || string.IsNullOrWhiteSpace(request.RequestFingerprint))
            throw new ImageAdapterValidationException("Complete image adapter request is required.");
    }

    private static void ValidateOutput(ImageAdapterOutput output)
    {
        if (output.OutputId == Guid.Empty || string.IsNullOrWhiteSpace(output.StorageRoot) || string.IsNullOrWhiteSpace(output.RelativePath) ||
            Path.IsPathRooted(output.RelativePath) || output.RelativePath.Split('/', '\\').Any(x => x == "..") ||
            string.IsNullOrWhiteSpace(output.MediaFormat) || output.Width < 1 || output.Height < 1 || output.Bytes < 1 ||
            string.IsNullOrWhiteSpace(output.ContentDigest))
            throw new ImageAdapterValidationException("Adapter output metadata or path is invalid.");
    }

    private static void History(SqliteConnection c, SqliteTransaction tx, ImageAdapterRequestState state, string operation, string actor, string reason, DateTimeOffset at) =>
        Exec(c, tx, "INSERT INTO image_adapter_history(workspace_id,history_id,request_id,revision,event_type,actor,reason,snapshot_json,occurred_at_utc) VALUES($w,$h,$id,$r,$e,$a,$reason,$s,$at)",
            ("$w",state.WorkspaceId),("$h",Guid.NewGuid().ToString("D")),("$id",state.RequestId.ToString("D")),("$r",state.Revision),
            ("$e",operation),("$a",actor),("$reason",reason),("$s",JsonSerializer.Serialize(state)),("$at",at.ToString("O")));

    private static void Receipt(SqliteConnection c, SqliteTransaction tx, string workspaceId, Guid requestId, Guid operationId,
        string fingerprint, string payload, ImageAdapterRequestState state, DateTimeOffset at) =>
        Exec(c, tx, "INSERT INTO image_adapter_receipts(workspace_id,request_id,operation_id,request_fingerprint,payload_digest,response_json,created_at_utc) VALUES($w,$id,$o,$f,$p,$r,$at)",
            ("$w",workspaceId),("$id",requestId.ToString("D")),("$o",operationId.ToString("D")),("$f",fingerprint),("$p",payload),
            ("$r",JsonSerializer.Serialize(state)),("$at",at.ToString("O")));

    private static void Outbox(SqliteConnection c, SqliteTransaction tx, ImageAdapterRequestState state, string type, DateTimeOffset at) =>
        Exec(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,created_at_utc) VALUES($id,$t,'1',$p,$at,$at,'PENDING',0,$at)",
            ("$id",state.MessageId!.Value.ToString("D")),("$t",type),("$p",JsonSerializer.Serialize(new { state.WorkspaceId, state.RequestId, state.ProjectId, state.VisualBriefId, state.Revision, state.Status })),("$at",at.ToString("O")));

    private static int Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object)[] values)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var value in values) cmd.Parameters.AddWithValue(value.Item1, value.Item2);
        return cmd.ExecuteNonQuery();
    }

    private static Guid MessageId(Guid requestId, long revision) => DeterministicGuid(requestId, $"message:{revision}");
    private static Guid DeterministicGuid(Guid id, string suffix)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"image-adapter:{id:D}:{suffix}"));
        return new Guid(bytes[..16]);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string EnumText<T>(T value) where T : struct, Enum => value.ToString().ToUpperInvariant();
    public ValueTask DisposeAsync() { _gate.Dispose(); return ValueTask.CompletedTask; }
}

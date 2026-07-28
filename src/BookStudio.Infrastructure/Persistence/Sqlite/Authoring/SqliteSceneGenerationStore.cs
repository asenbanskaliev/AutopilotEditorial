using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteSceneGenerationStore : ISceneGenerationStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteSceneGenerationStore(SqliteConnectionFactory factory, int writeQueueCapacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, writeQueueCapacity);
    }

    public ValueTask<SceneGenerationCreateResult> CreateAsync(SceneGenerationDraft draft, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(draft);
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, draft.WorkspaceId, draft.GenerationId);
            if (existing is not null)
            {
                if (existing.ProjectId == draft.ProjectId && existing.ScenePlanId == draft.ScenePlanId && existing.ScenePlanVersion == draft.ScenePlanVersion && existing.ScenePlanApprovalMessageId == draft.ScenePlanApprovalMessageId && existing.ScenePlanContentDigest == draft.ScenePlanContentDigest && existing.SchemaVersion == draft.SchemaVersion && Same(existing.Brief, draft.Brief))
                    return new SceneGenerationCreateResult(existing, true);
                throw new SceneGenerationConflictException("Scene generation identity already exists with different immutable content.");
            }
            RequireApprovedScenePlan(c, tx, draft);
            Execute(c, tx, "INSERT INTO scene_generations(workspace_id,generation_id,project_id,scene_plan_id,scene_plan_version,scene_plan_approval_message_id,scene_plan_content_digest,schema_version,brief_json,revision,status,approval_message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$sp,$v,$m,$d,$s,$b,1,'PLANNED',NULL,$at,$at);",
                ("$w",draft.WorkspaceId),("$id",draft.GenerationId.ToString("D")),("$p",draft.ProjectId.ToString("D")),("$sp",draft.ScenePlanId.ToString("D")),("$v",draft.ScenePlanVersion),("$m",draft.ScenePlanApprovalMessageId.ToString("D")),("$d",draft.ScenePlanContentDigest),("$s",draft.SchemaVersion),("$b",JsonSerializer.Serialize(draft.Brief)),("$at",Text(at)));
            InsertRequest(c, tx, CreateRequestId(draft), draft.WorkspaceId, draft.GenerationId, "CREATE", draft.RequestFingerprint, 1, null, null, at);
            return new SceneGenerationCreateResult(Require(c, tx, draft.WorkspaceId, draft.GenerationId), false);
        }, ct);
    }

    public ValueTask<SceneGeneration> StartAttemptAsync(SceneGenerationStartCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(command.RequestId, command.WorkspaceId, command.GenerationId, command.Actor, command.RequestFingerprint);
        ValidateInvocation(command.Invocation);
        return Mutate(command.RequestId, command.WorkspaceId, command.GenerationId, "START", command.RequestFingerprint, at, (c, tx, current) =>
        {
            RequireRevision(current, command.ExpectedRevision);
            if (current.Status is not (SceneGenerationStatus.Planned or SceneGenerationStatus.Failed)) throw new SceneGenerationTransitionException("A generation attempt can only start from planned or failed state.");
            if (current.Status == SceneGenerationStatus.Failed && current.Attempts.Last().Retryable != true) throw new SceneGenerationTransitionException("A non-retryable failure cannot start another attempt.");
            var attempt = current.Attempts.Count + 1;
            Execute(c, tx, "INSERT INTO scene_generation_attempts(workspace_id,generation_id,attempt,status,invocation_json,generated_text,content_digest,acceptance_evidence_json,error_class,error_text,retryable,actor,started_at_utc,finished_at_utc) VALUES($w,$id,$a,'RUNNING',$i,NULL,NULL,'[]',NULL,NULL,NULL,$actor,$at,NULL);",
                ("$w",command.WorkspaceId),("$id",command.GenerationId.ToString("D")),("$a",attempt),("$i",JsonSerializer.Serialize(command.Invocation)),("$actor",command.Actor),("$at",Text(at)));
            UpdateState(c, tx, command.WorkspaceId, command.GenerationId, current.Revision + 1, SceneGenerationStatus.Generating, at);
        }, ct);
    }

    public ValueTask<SceneGeneration> CompleteAttemptAsync(SceneGenerationCompleteCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(command.RequestId, command.WorkspaceId, command.GenerationId, command.Actor, command.RequestFingerprint);
        if (string.IsNullOrWhiteSpace(command.GeneratedText) || command.AcceptanceEvidence is null) throw new SceneGenerationValidationException("Generated text and acceptance evidence are required.");
        return Mutate(command.RequestId, command.WorkspaceId, command.GenerationId, "COMPLETE", command.RequestFingerprint, at, (c, tx, current) =>
        {
            RequireRevision(current, command.ExpectedRevision);
            var attempt = RequireRunningAttempt(current, command.ExpectedAttempt);
            RequireAcceptance(current.Brief, command.AcceptanceEvidence);
            var digest = Digest(command.GeneratedText);
            Execute(c, tx, "UPDATE scene_generation_attempts SET status='GENERATED',generated_text=$t,content_digest=$d,acceptance_evidence_json=$e,finished_at_utc=$at WHERE workspace_id=$w AND generation_id=$id AND attempt=$a AND status='RUNNING';",
                ("$t",command.GeneratedText),("$d",digest),("$e",JsonSerializer.Serialize(command.AcceptanceEvidence)),("$at",Text(at)),("$w",command.WorkspaceId),("$id",command.GenerationId.ToString("D")),("$a",attempt.Attempt));
            UpdateState(c, tx, command.WorkspaceId, command.GenerationId, current.Revision + 1, SceneGenerationStatus.Generated, at);
        }, ct);
    }

    public ValueTask<SceneGeneration> FailAttemptAsync(SceneGenerationFailCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(command.RequestId, command.WorkspaceId, command.GenerationId, command.Actor, command.RequestFingerprint);
        if (string.IsNullOrWhiteSpace(command.ErrorClass) || string.IsNullOrWhiteSpace(command.ErrorText)) throw new SceneGenerationValidationException("Failure class and text are required.");
        return Mutate(command.RequestId, command.WorkspaceId, command.GenerationId, "FAIL", command.RequestFingerprint, at, (c, tx, current) =>
        {
            RequireRevision(current, command.ExpectedRevision);
            var attempt = RequireRunningAttempt(current, command.ExpectedAttempt);
            Execute(c, tx, "UPDATE scene_generation_attempts SET status='FAILED',error_class=$c,error_text=$e,retryable=$r,finished_at_utc=$at WHERE workspace_id=$w AND generation_id=$id AND attempt=$a AND status='RUNNING';",
                ("$c",command.ErrorClass),("$e",command.ErrorText),("$r",command.Retryable?1:0),("$at",Text(at)),("$w",command.WorkspaceId),("$id",command.GenerationId.ToString("D")),("$a",attempt.Attempt));
            UpdateState(c, tx, command.WorkspaceId, command.GenerationId, current.Revision + 1, SceneGenerationStatus.Failed, at);
        }, ct);
    }

    public ValueTask<SceneGeneration> SubmitAsync(SceneGenerationSubmitCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(command.RequestId, command.WorkspaceId, command.GenerationId, command.Actor, command.RequestFingerprint);
        return Mutate(command.RequestId, command.WorkspaceId, command.GenerationId, "SUBMIT", command.RequestFingerprint, at, (c, tx, current) =>
        {
            RequireRevision(current, command.ExpectedRevision);
            if (current.Status != SceneGenerationStatus.Generated) throw new SceneGenerationTransitionException("Only generated content can be submitted.");
            UpdateState(c, tx, command.WorkspaceId, command.GenerationId, current.Revision + 1, SceneGenerationStatus.Submitted, at);
        }, ct);
    }

    public ValueTask<SceneGenerationApprovalResult> ApproveAsync(SceneGenerationApprovalCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(command.RequestId, command.WorkspaceId, command.GenerationId, command.Actor, command.RequestFingerprint);
        if (string.IsNullOrWhiteSpace(command.Reason)) throw new SceneGenerationValidationException("Approval reason is required.");
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var prior = ReadRequest(c, tx, command.RequestId);
            if (prior is not null)
            {
                RequireRequest(prior, "APPROVE", command.WorkspaceId, command.GenerationId, command.RequestFingerprint);
                return new SceneGenerationApprovalResult(Require(c, tx, command.WorkspaceId, command.GenerationId), true, prior.ApprovalMessageId ?? throw new InvalidOperationException("Approval receipt is missing message identity."));
            }
            var current = Require(c, tx, command.WorkspaceId, command.GenerationId);
            RequireRevision(current, command.ExpectedRevision);
            if (current.Status != SceneGenerationStatus.Submitted) throw new SceneGenerationTransitionException("Only submitted content can be approved.");
            var generated = current.Attempts.LastOrDefault(x => x.Status == SceneAttemptStatus.Generated) ?? throw new SceneGenerationTransitionException("Approved content requires a generated attempt.");
            var messageId = MessageId(command.RequestId);
            UpdateState(c, tx, command.WorkspaceId, command.GenerationId, current.Revision + 1, SceneGenerationStatus.Approved, at, messageId);
            Execute(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($m,'editorial.scene.approved','1.0.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);",
                ("$m",messageId.ToString("D")),("$p",JsonSerializer.Serialize(new { command.WorkspaceId, command.GenerationId, current.ProjectId, current.ScenePlanId, current.Brief.SceneKey, current.ScenePlanVersion, attempt=generated.Attempt, contentDigest=generated.ContentDigest, approvedBy=command.Actor, command.Reason })),("$at",Text(at)));
            InsertRequest(c, tx, command.RequestId, command.WorkspaceId, command.GenerationId, "APPROVE", command.RequestFingerprint, current.Revision + 1, generated.Attempt, messageId, at);
            return new SceneGenerationApprovalResult(Require(c, tx, command.WorkspaceId, command.GenerationId), false, messageId);
        }, ct);
    }

    public async ValueTask<SceneGeneration?> GetAsync(string workspaceId, Guid generationId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var c = _factory.OpenConnection(); return await Task.FromResult(Read(c, null, workspaceId, generationId)).ConfigureAwait(false); }

    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false); }

    private ValueTask<SceneGeneration> Mutate(Guid requestId, string w, Guid id, string op, string fingerprint, DateTimeOffset at, Action<SqliteConnection,SqliteTransaction,SceneGeneration> action, CancellationToken ct) =>
        _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var prior=ReadRequest(c,tx,requestId); if(prior is not null){RequireRequest(prior,op,w,id,fingerprint); return Require(c,tx,w,id);}
            var current=Require(c,tx,w,id); action(c,tx,current); var updated=Require(c,tx,w,id);
            InsertRequest(c,tx,requestId,w,id,op,fingerprint,updated.Revision,updated.Attempts.LastOrDefault()?.Attempt,updated.ApprovalMessageId,at); return updated;
        },ct);

    private sealed record RequestRow(string WorkspaceId, Guid GenerationId, string Operation, string Fingerprint, Guid? ApprovalMessageId);
    private static SceneGeneration Require(SqliteConnection c, SqliteTransaction tx, string w, Guid id)=>Read(c,tx,w,id)??throw new KeyNotFoundException("Scene generation was not found.");
    private static SceneGeneration? Read(SqliteConnection c, SqliteTransaction? tx, string w, Guid id)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT project_id,scene_plan_id,scene_plan_version,scene_plan_approval_message_id,scene_plan_content_digest,schema_version,brief_json,revision,status,approval_message_id,created_at_utc,updated_at_utc FROM scene_generations WHERE workspace_id=$w AND generation_id=$id;";cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$id",id.ToString("D"));using var r=cmd.ExecuteReader();if(!r.Read())return null;
        var project=Guid.Parse(r.GetString(0));var plan=Guid.Parse(r.GetString(1));var planVersion=r.GetInt64(2);var planMessage=Guid.Parse(r.GetString(3));var planDigest=r.GetString(4);var schema=r.GetString(5);var brief=JsonSerializer.Deserialize<SceneGenerationBrief>(r.GetString(6))??throw new InvalidOperationException("Invalid scene brief.");var revision=r.GetInt64(7);var status=Enum.Parse<SceneGenerationStatus>(r.GetString(8),true);Guid? approval=r.IsDBNull(9)?null:Guid.Parse(r.GetString(9));var created=Parse(r.GetString(10));var updated=Parse(r.GetString(11));r.Close();
        using var a=c.CreateCommand();a.Transaction=tx;a.CommandText="SELECT attempt,status,invocation_json,generated_text,content_digest,acceptance_evidence_json,error_class,error_text,retryable,actor,started_at_utc,finished_at_utc FROM scene_generation_attempts WHERE workspace_id=$w AND generation_id=$id ORDER BY attempt;";a.Parameters.AddWithValue("$w",w);a.Parameters.AddWithValue("$id",id.ToString("D"));using var ar=a.ExecuteReader();var attempts=new List<SceneGenerationAttempt>();while(ar.Read())attempts.Add(new(ar.GetInt64(0),Enum.Parse<SceneAttemptStatus>(ar.GetString(1),true),JsonSerializer.Deserialize<SceneInvocation>(ar.GetString(2))!,ar.IsDBNull(3)?null:ar.GetString(3),ar.IsDBNull(4)?null:ar.GetString(4),JsonSerializer.Deserialize<IReadOnlyList<AcceptanceEvidence>>(ar.GetString(5))??Array.Empty<AcceptanceEvidence>(),ar.IsDBNull(6)?null:ar.GetString(6),ar.IsDBNull(7)?null:ar.GetString(7),ar.IsDBNull(8)?null:ar.GetInt64(8)!=0,ar.GetString(9),Parse(ar.GetString(10)),ar.IsDBNull(11)?null:Parse(ar.GetString(11))));
        return new(id,project,plan,planVersion,planMessage,planDigest,w,schema,brief,revision,status,attempts,approval,created,updated);
    }
    private static RequestRow? ReadRequest(SqliteConnection c,SqliteTransaction tx,Guid id){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT workspace_id,generation_id,operation,request_fingerprint,approval_message_id FROM scene_generation_requests WHERE request_id=$id;";cmd.Parameters.AddWithValue("$id",id.ToString("D"));using var r=cmd.ExecuteReader();return r.Read()?new(r.GetString(0),Guid.Parse(r.GetString(1)),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:Guid.Parse(r.GetString(4))):null;}
    private static void InsertRequest(SqliteConnection c,SqliteTransaction tx,Guid request,string w,Guid id,string op,string fingerprint,long revision,long? attempt,Guid? message,DateTimeOffset at)=>Execute(c,tx,"INSERT INTO scene_generation_requests(request_id,workspace_id,generation_id,operation,request_fingerprint,result_revision,result_attempt,approval_message_id,created_at_utc) VALUES($r,$w,$id,$op,$f,$rev,$a,$m,$at);",("$r",request.ToString("D")),("$w",w),("$id",id.ToString("D")),("$op",op),("$f",fingerprint),("$rev",revision),("$a",attempt is null?DBNull.Value:attempt.Value),("$m",message is null?DBNull.Value:message.Value.ToString("D")),("$at",Text(at)));
    private static void UpdateState(SqliteConnection c,SqliteTransaction tx,string w,Guid id,long revision,SceneGenerationStatus status,DateTimeOffset at,Guid? approval=null)=>Execute(c,tx,"UPDATE scene_generations SET revision=$r,status=$s,approval_message_id=COALESCE($m,approval_message_id),updated_at_utc=$at WHERE workspace_id=$w AND generation_id=$id;",("$r",revision),("$s",status.ToString().ToUpperInvariant()),("$m",approval is null?DBNull.Value:approval.Value.ToString("D")),("$at",Text(at)),("$w",w),("$id",id.ToString("D")));
    private static void RequireApprovedScenePlan(SqliteConnection c,SqliteTransaction tx,SceneGenerationDraft d){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT current_version,approval_message_id FROM scene_plans WHERE workspace_id=$w AND scene_plan_id=$id AND project_id=$p;";cmd.Parameters.AddWithValue("$w",d.WorkspaceId);cmd.Parameters.AddWithValue("$id",d.ScenePlanId.ToString("D"));cmd.Parameters.AddWithValue("$p",d.ProjectId.ToString("D"));using var r=cmd.ExecuteReader();if(!r.Read()||r.GetInt64(0)!=d.ScenePlanVersion||r.IsDBNull(1)||Guid.Parse(r.GetString(1))!=d.ScenePlanApprovalMessageId)throw new SceneGenerationValidationException("An approved scene plan with matching version and approval evidence is required.");}
    private static SceneGenerationAttempt RequireRunningAttempt(SceneGeneration g,long expected){if(g.Status!=SceneGenerationStatus.Generating||g.Attempts.Count==0||g.Attempts[^1].Attempt!=expected||g.Attempts[^1].Status!=SceneAttemptStatus.Running)throw new SceneGenerationConflictException("Expected running attempt is stale or missing.");return g.Attempts[^1];}
    private static void RequireAcceptance(SceneGenerationBrief brief,IReadOnlyList<AcceptanceEvidence> evidence){var keys=new HashSet<string>(evidence.Select(x=>x.Criterion),StringComparer.Ordinal);if(brief.AcceptanceCriteria.Any(x=>!keys.Contains(x))||evidence.Any(x=>string.IsNullOrWhiteSpace(x.Criterion)||string.IsNullOrWhiteSpace(x.Evidence)))throw new SceneGenerationValidationException("Every acceptance criterion requires attributable evidence.");}
    private static void RequireRevision(SceneGeneration g,long revision){if(g.Revision!=revision)throw new SceneGenerationConflictException("Expected scene generation revision is stale.");}
    private static void RequireRequest(RequestRow r,string op,string w,Guid id,string fingerprint){if(r.Operation!=op||r.WorkspaceId!=w||r.GenerationId!=id||r.Fingerprint!=fingerprint)throw new SceneGenerationConflictException("Request ID was reused with different immutable content.");}
    private static void ValidateDraft(SceneGenerationDraft d){if(d is null||d.GenerationId==Guid.Empty||d.ProjectId==Guid.Empty||d.ScenePlanId==Guid.Empty||d.ScenePlanVersion<=0||d.ScenePlanApprovalMessageId==Guid.Empty||string.IsNullOrWhiteSpace(d.ScenePlanContentDigest)||string.IsNullOrWhiteSpace(d.WorkspaceId)||string.IsNullOrWhiteSpace(d.SchemaVersion)||string.IsNullOrWhiteSpace(d.Actor)||string.IsNullOrWhiteSpace(d.RequestFingerprint))throw new SceneGenerationValidationException("Scene generation draft is incomplete.");ValidateBrief(d.Brief);}
    private static void ValidateBrief(SceneGenerationBrief b){if(b is null||string.IsNullOrWhiteSpace(b.SceneKey)||string.IsNullOrWhiteSpace(b.ChapterKey)||b.Order<=0||string.IsNullOrWhiteSpace(b.Title)||string.IsNullOrWhiteSpace(b.Purpose)||string.IsNullOrWhiteSpace(b.Summary)||b.Beats is null||b.Beats.Count==0||b.RequiredEvidence is null||b.Constraints is null||b.AcceptanceCriteria is null||b.AcceptanceCriteria.Count==0)throw new SceneGenerationValidationException("Scene generation brief is incomplete.");}
    private static void ValidateInvocation(SceneInvocation i){if(i is null||string.IsNullOrWhiteSpace(i.Provider)||string.IsNullOrWhiteSpace(i.Model)||string.IsNullOrWhiteSpace(i.PromptTemplateVersion)||string.IsNullOrWhiteSpace(i.CompiledContextDigest)||string.IsNullOrWhiteSpace(i.ParametersJson)||string.IsNullOrWhiteSpace(i.PolicyProfile))throw new SceneGenerationValidationException("Scene invocation is incomplete.");try{JsonDocument.Parse(i.ParametersJson);}catch(JsonException ex){throw new SceneGenerationValidationException("Invocation parameters must be valid JSON: "+ex.Message);}}
    private static void ValidateRequest(Guid request,string w,Guid id,string actor,string fingerprint){if(request==Guid.Empty||id==Guid.Empty||string.IsNullOrWhiteSpace(w)||string.IsNullOrWhiteSpace(actor)||string.IsNullOrWhiteSpace(fingerprint))throw new SceneGenerationValidationException("Scene generation request is incomplete.");}
    private static bool Same(SceneGenerationBrief a,SceneGenerationBrief b)=>JsonSerializer.Serialize(a)==JsonSerializer.Serialize(b);
    private static string Digest(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static Guid MessageId(Guid request)=>new(SHA256.HashData(Encoding.UTF8.GetBytes("scene-generation-approval:"+request.ToString("D")))[..16]);
    private static Guid CreateRequestId(SceneGenerationDraft d)=>new(SHA256.HashData(Encoding.UTF8.GetBytes("scene-generation-create:"+d.WorkspaceId+":"+d.GenerationId.ToString("D")))[..16]);
    private static string Text(DateTimeOffset v)=>v.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);private static DateTimeOffset Parse(string v)=>DateTimeOffset.Parse(v,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
    private static void Execute(SqliteConnection c,SqliteTransaction tx,string sql,params (string Name,object Value)[] values){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var x in values)cmd.Parameters.AddWithValue(x.Name,x.Value);cmd.ExecuteNonQuery();}
}

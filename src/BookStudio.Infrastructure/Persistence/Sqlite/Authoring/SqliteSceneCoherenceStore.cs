using System.Globalization;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteSceneCoherenceStore : ISceneCoherenceStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteSceneCoherenceStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<SceneCoherenceCreateResult> CreateAsync(SceneCoherenceDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(d);
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, d.WorkspaceId, d.AuditId);
            if (existing is not null)
            {
                if (existing.ProjectId == d.ProjectId && existing.GenerationId == d.GenerationId && existing.SceneApprovalMessageId == d.SceneApprovalMessageId && existing.SceneContentDigest == d.SceneContentDigest && existing.ScenePlanId == d.ScenePlanId && existing.ScenePlanVersion == d.ScenePlanVersion && existing.SceneKey == d.SceneKey && existing.RuleSetVersion == d.RuleSetVersion && existing.SourceText == d.SourceText)
                    return new SceneCoherenceCreateResult(existing, true);
                throw new SceneCoherenceConflictException("Audit identity already exists with different immutable content.");
            }

            var authority = LoadAuthority(c, tx, d);
            Execute(c, tx, "INSERT INTO scene_coherence_audits(workspace_id,audit_id,project_id,generation_id,scene_approval_message_id,scene_content_digest,scene_plan_id,scene_plan_version,scene_key,rule_set_version,source_text,entry_state,exit_state,planned_beats_json,beat_assessments_json,causal_links_json,findings_json,revision,status,closed_message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$g,$m,$d,$sp,$v,$k,$r,$s,$e,$x,$b,'[]','[]','[]',1,'DRAFT',NULL,$at,$at);",
                ("$w", d.WorkspaceId), ("$id", d.AuditId.ToString("D")), ("$p", d.ProjectId.ToString("D")), ("$g", d.GenerationId.ToString("D")), ("$m", d.SceneApprovalMessageId.ToString("D")), ("$d", d.SceneContentDigest), ("$sp", d.ScenePlanId.ToString("D")), ("$v", d.ScenePlanVersion), ("$k", d.SceneKey), ("$r", d.RuleSetVersion), ("$s", d.SourceText), ("$e", authority.EntryState), ("$x", authority.ExitState), ("$b", JsonSerializer.Serialize(authority.Beats)), ("$at", Text(at)));
            InsertReceipt(c, tx, d.AuditId, d.WorkspaceId, d.AuditId, "CREATE", d.RequestFingerprint, 1, null, at);
            return new SceneCoherenceCreateResult(Require(c, tx, d.WorkspaceId, d.AuditId), false);
        }, ct);
    }

    public ValueTask<SceneCoherenceAudit> StartAsync(SceneCoherenceControlCommand c, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(c.RequestId,c.WorkspaceId,c.AuditId,"START",c.RequestFingerprint,c.ExpectedRevision,at,(a,b,l,f)=>
        {
            if(a.Status!=SceneCoherenceStatus.Draft) throw new SceneCoherenceTransitionException("Only draft audits can start.");
            return (SceneCoherenceStatus.Running,b,l,f,null);
        },ct);

    public ValueTask<SceneCoherenceAudit> AssessBeatAsync(SceneBeatAssessmentCommand c, DateTimeOffset at, CancellationToken ct = default)
    {
        if(string.IsNullOrWhiteSpace(c.BeatKey)||c.PlannedOrder<=0||string.IsNullOrWhiteSpace(c.Evidence)) throw new SceneCoherenceValidationException("Beat identity, order and evidence are required.");
        return Mutate(c.RequestId,c.WorkspaceId,c.AuditId,"ASSESS_BEAT",c.RequestFingerprint,c.ExpectedRevision,at,(a,b,l,f)=>
        {
            if(a.Status!=SceneCoherenceStatus.Running) throw new SceneCoherenceTransitionException("Beat assessment requires a running audit.");
            var index=a.PlannedBeats.ToList().FindIndex(x=>x==c.BeatKey);
            if(index<0 || index+1!=c.PlannedOrder) throw new SceneCoherenceValidationException("Beat does not match the approved ScenePlan order.");
            ValidateRange(c.StartOffset,c.Length,a.SourceText.Length);
            if(b.Any(x=>x.BeatKey==c.BeatKey)) throw new SceneCoherenceConflictException("Beat was already assessed.");
            b.Add(new(c.BeatKey,c.PlannedOrder,c.Status,c.StartOffset,c.Length,c.Evidence,c.Actor,at));
            return (a.Status,b,l,f,null);
        },ct);
    }

    public ValueTask<SceneCoherenceAudit> RecordCausalLinkAsync(SceneCausalLinkCommand c, DateTimeOffset at, CancellationToken ct = default)
    {
        if(c.LinkId==Guid.Empty||string.IsNullOrWhiteSpace(c.Evidence)) throw new SceneCoherenceValidationException("Causal link identity and evidence are required.");
        return Mutate(c.RequestId,c.WorkspaceId,c.AuditId,"CAUSAL_LINK",c.RequestFingerprint,c.ExpectedRevision,at,(a,b,l,f)=>
        {
            if(a.Status!=SceneCoherenceStatus.Running) throw new SceneCoherenceTransitionException("Causal links require a running audit.");
            ValidateRange(c.CauseStartOffset,c.CauseLength,a.SourceText.Length); ValidateRange(c.EffectStartOffset,c.EffectLength,a.SourceText.Length);
            if(c.EffectStartOffset<=c.CauseStartOffset) throw new SceneCoherenceValidationException("Effect must follow cause in scene order.");
            if(l.Any(x=>x.LinkId==c.LinkId)) throw new SceneCoherenceConflictException("Causal link identity already exists.");
            l.Add(new(c.LinkId,c.CauseStartOffset,c.CauseLength,c.EffectStartOffset,c.EffectLength,c.Status,c.Evidence,c.Actor,at));
            return (a.Status,b,l,f,null);
        },ct);
    }

    public ValueTask<SceneCoherenceAudit> RecordFindingAsync(SceneCoherenceFindingCommand c, DateTimeOffset at, CancellationToken ct = default)
    {
        if(c.FindingId==Guid.Empty||string.IsNullOrWhiteSpace(c.RuleId)||string.IsNullOrWhiteSpace(c.RuleVersion)||string.IsNullOrWhiteSpace(c.Evidence)||string.IsNullOrWhiteSpace(c.Recommendation)) throw new SceneCoherenceValidationException("Complete finding attribution is required.");
        return Mutate(c.RequestId,c.WorkspaceId,c.AuditId,"FIND",c.RequestFingerprint,c.ExpectedRevision,at,(a,b,l,f)=>
        {
            if(a.Status!=SceneCoherenceStatus.Running) throw new SceneCoherenceTransitionException("Findings require a running audit.");
            ValidateRange(c.StartOffset,c.Length,a.SourceText.Length);
            if(f.Any(x=>x.FindingId==c.FindingId)) throw new SceneCoherenceConflictException("Finding identity already exists.");
            f.Add(new(c.FindingId,c.RuleId,c.RuleVersion,c.Category,c.Severity,c.StartOffset,c.Length,c.Evidence,c.Recommendation,SceneCoherenceDecision.Open,null,c.Actor,at,null));
            return (a.Status,b,l,f,null);
        },ct);
    }

    public ValueTask<SceneCoherenceAudit> DecideFindingAsync(SceneCoherenceDecisionCommand c, DateTimeOffset at, CancellationToken ct = default)
    {
        if(c.Decision==SceneCoherenceDecision.Open||string.IsNullOrWhiteSpace(c.Reason)) throw new SceneCoherenceValidationException("A terminal decision and reason are required.");
        return Mutate(c.RequestId,c.WorkspaceId,c.AuditId,"DECIDE",c.RequestFingerprint,c.ExpectedRevision,at,(a,b,l,f)=>
        {
            if(a.Status is SceneCoherenceStatus.Draft or SceneCoherenceStatus.Closed) throw new SceneCoherenceTransitionException("Finding decisions require an active audit.");
            var i=f.FindIndex(x=>x.FindingId==c.FindingId); if(i<0) throw new KeyNotFoundException("Finding not found.");
            if(f[i].Decision!=SceneCoherenceDecision.Open) throw new SceneCoherenceConflictException("Finding already decided.");
            f[i]=f[i] with { Decision=c.Decision, DecisionReason=c.Reason, DecidedAtUtc=at };
            return (a.Status,b,l,f,null);
        },ct);
    }

    public ValueTask<SceneCoherenceAudit> ReviewAsync(SceneCoherenceControlCommand c, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(c.RequestId,c.WorkspaceId,c.AuditId,"REVIEW",c.RequestFingerprint,c.ExpectedRevision,at,(a,b,l,f)=>
        {
            if(a.Status!=SceneCoherenceStatus.Running) throw new SceneCoherenceTransitionException("Only running audits can be reviewed.");
            if(b.Count!=a.PlannedBeats.Count) throw new SceneCoherenceTransitionException("Every planned beat must be assessed.");
            return (SceneCoherenceStatus.Reviewed,b,l,f,null);
        },ct);

    public ValueTask<SceneCoherenceCloseResult> CloseAsync(SceneCoherenceCloseCommand c, DateTimeOffset at, CancellationToken ct = default)
    {
        if(string.IsNullOrWhiteSpace(c.Reason)) throw new SceneCoherenceValidationException("Close reason is required.");
        return _queue.ExecuteInTransactionAsync((db,tx,token)=>
        {
            token.ThrowIfCancellationRequested();
            var receipt=ReadReceipt(db,tx,c.RequestId);
            if(receipt is not null){RequireReceipt(receipt,"CLOSE",c.WorkspaceId,c.AuditId,c.RequestFingerprint);var replay=Require(db,tx,c.WorkspaceId,c.AuditId);return new SceneCoherenceCloseResult(replay,true,receipt.MessageId??throw new InvalidOperationException("Close receipt lacks message id."));}
            var a=Require(db,tx,c.WorkspaceId,c.AuditId); RequireRevision(a,c.ExpectedRevision);
            if(a.Status!=SceneCoherenceStatus.Reviewed) throw new SceneCoherenceTransitionException("Only reviewed audits can close.");
            if(a.BeatAssessments.Any(x=>x.Status is SceneBeatStatus.Missing or SceneBeatStatus.OutOfOrder) || a.CausalLinks.Any(x=>x.Status is SceneCausalStatus.Broken or SceneCausalStatus.Reversed or SceneCausalStatus.Unsupported) || a.Findings.Any(x=>x.Severity==SceneCoherenceSeverity.Blocking&&x.Decision==SceneCoherenceDecision.Open)) throw new SceneCoherenceTransitionException("Blocking coherence defects must be resolved before close.");
            var message=MessageId(c.RequestId); Update(db,tx,a,a.BeatAssessments.ToList(),a.CausalLinks.ToList(),a.Findings.ToList(),SceneCoherenceStatus.Closed,a.Revision+1,at,message);
            Execute(db,tx,"INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($m,'editorial.scene-coherence.closed','1.0.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);",("$m",message.ToString("D")),("$p",JsonSerializer.Serialize(new{c.WorkspaceId,c.AuditId,a.ProjectId,a.GenerationId,a.ScenePlanId,a.ScenePlanVersion,a.SceneKey,a.SceneContentDigest,closedBy=c.Actor,c.Reason})),("$at",Text(at)));
            InsertReceipt(db,tx,c.RequestId,c.WorkspaceId,c.AuditId,"CLOSE",c.RequestFingerprint,a.Revision+1,message,at);
            return new SceneCoherenceCloseResult(Require(db,tx,c.WorkspaceId,c.AuditId),false,message);
        },ct);
    }

    public async ValueTask<SceneCoherenceAudit?> GetAsync(string workspaceId, Guid auditId, CancellationToken ct = default){ct.ThrowIfCancellationRequested();using var c=_factory.OpenConnection();return await Task.FromResult(Read(c,null,workspaceId,auditId));}
    public async ValueTask DisposeAsync(){if(Interlocked.Exchange(ref _disposed,1)==0)await _queue.DisposeAsync().ConfigureAwait(false);}

    private ValueTask<SceneCoherenceAudit> Mutate(Guid requestId,string workspace,Guid auditId,string op,string fingerprint,long expected,DateTimeOffset at,Func<SceneCoherenceAudit,List<SceneBeatAssessment>,List<SceneCausalLink>,List<SceneCoherenceFinding>,(SceneCoherenceStatus,List<SceneBeatAssessment>,List<SceneCausalLink>,List<SceneCoherenceFinding>,Guid?)> mutation,CancellationToken ct)
    {
        if(requestId==Guid.Empty||auditId==Guid.Empty||string.IsNullOrWhiteSpace(workspace)||string.IsNullOrWhiteSpace(fingerprint)) throw new SceneCoherenceValidationException("Request identity is required.");
        return _queue.ExecuteInTransactionAsync((c,tx,token)=>
        {
            token.ThrowIfCancellationRequested();var receipt=ReadReceipt(c,tx,requestId);if(receipt is not null){RequireReceipt(receipt,op,workspace,auditId,fingerprint);return Require(c,tx,workspace,auditId);}var a=Require(c,tx,workspace,auditId);RequireRevision(a,expected);var result=mutation(a,a.BeatAssessments.ToList(),a.CausalLinks.ToList(),a.Findings.ToList());Update(c,tx,a,result.Item2,result.Item3,result.Item4,result.Item1,a.Revision+1,at,result.Item5);InsertReceipt(c,tx,requestId,workspace,auditId,op,fingerprint,a.Revision+1,result.Item5,at);return Require(c,tx,workspace,auditId);
        },ct);
    }

    private static Authority LoadAuthority(SqliteConnection c,SqliteTransaction tx,SceneCoherenceDraft d)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT v.content_json,g.brief_json FROM scene_generations g JOIN scene_generation_attempts a ON a.workspace_id=g.workspace_id AND a.generation_id=g.generation_id JOIN scene_plan_versions v ON v.workspace_id=g.workspace_id AND v.scene_plan_id=g.scene_plan_id AND v.version=g.scene_plan_version WHERE g.workspace_id=$w AND g.generation_id=$g AND g.project_id=$p AND g.status='APPROVED' AND g.approval_message_id=$m AND g.scene_plan_id=$sp AND g.scene_plan_version=$v AND a.status='GENERATED' AND a.content_digest=$d AND a.generated_text=$t AND v.status='APPROVED';";cmd.Parameters.AddWithValue("$w",d.WorkspaceId);cmd.Parameters.AddWithValue("$g",d.GenerationId.ToString("D"));cmd.Parameters.AddWithValue("$p",d.ProjectId.ToString("D"));cmd.Parameters.AddWithValue("$m",d.SceneApprovalMessageId.ToString("D"));cmd.Parameters.AddWithValue("$sp",d.ScenePlanId.ToString("D"));cmd.Parameters.AddWithValue("$v",d.ScenePlanVersion);cmd.Parameters.AddWithValue("$d",d.SceneContentDigest);cmd.Parameters.AddWithValue("$t",d.SourceText);using var r=cmd.ExecuteReader();if(!r.Read())throw new SceneCoherenceValidationException("Exact approved scene and ScenePlan authority was not found.");var plan=JsonSerializer.Deserialize<ScenePlanContent>(r.GetString(0))??throw new SceneCoherenceValidationException("ScenePlan content is invalid.");var scene=plan.Scenes.SingleOrDefault(x=>x.Key==d.SceneKey)??throw new SceneCoherenceValidationException("Scene key is absent from approved ScenePlan.");var entry=scene.DependsOn.Count==0?"ROOT":string.Join(",",scene.DependsOn);var exit=scene.AcceptanceCriteria.Count==0?scene.Summary:string.Join(" | ",scene.AcceptanceCriteria);return new(entry,exit,scene.Beats);
    }

    private static SceneCoherenceAudit? Read(SqliteConnection c,SqliteTransaction? tx,string w,Guid id){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT project_id,generation_id,scene_approval_message_id,scene_content_digest,scene_plan_id,scene_plan_version,scene_key,rule_set_version,source_text,entry_state,exit_state,planned_beats_json,beat_assessments_json,causal_links_json,findings_json,revision,status,closed_message_id,created_at_utc,updated_at_utc FROM scene_coherence_audits WHERE workspace_id=$w AND audit_id=$id;";cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$id",id.ToString("D"));using var r=cmd.ExecuteReader();if(!r.Read())return null;return new(id,Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),Guid.Parse(r.GetString(2)),r.GetString(3),Guid.Parse(r.GetString(4)),r.GetInt64(5),r.GetString(6),w,r.GetString(7),r.GetString(8),r.GetString(9),r.GetString(10),JsonSerializer.Deserialize<IReadOnlyList<string>>(r.GetString(11))??Array.Empty<string>(),JsonSerializer.Deserialize<IReadOnlyList<SceneBeatAssessment>>(r.GetString(12))??Array.Empty<SceneBeatAssessment>(),JsonSerializer.Deserialize<IReadOnlyList<SceneCausalLink>>(r.GetString(13))??Array.Empty<SceneCausalLink>(),JsonSerializer.Deserialize<IReadOnlyList<SceneCoherenceFinding>>(r.GetString(14))??Array.Empty<SceneCoherenceFinding>(),r.GetInt64(15),Enum.Parse<SceneCoherenceStatus>(r.GetString(16),true),r.IsDBNull(17)?null:Guid.Parse(r.GetString(17)),Parse(r.GetString(18)),Parse(r.GetString(19)));}
    private static SceneCoherenceAudit Require(SqliteConnection c,SqliteTransaction tx,string w,Guid id)=>Read(c,tx,w,id)??throw new KeyNotFoundException("Scene coherence audit not found.");
    private static void Update(SqliteConnection c,SqliteTransaction tx,SceneCoherenceAudit a,List<SceneBeatAssessment>b,List<SceneCausalLink>l,List<SceneCoherenceFinding>f,SceneCoherenceStatus s,long rev,DateTimeOffset at,Guid? message)=>Execute(c,tx,"UPDATE scene_coherence_audits SET beat_assessments_json=$b,causal_links_json=$l,findings_json=$f,status=$s,revision=$r,closed_message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND audit_id=$id;",("$b",JsonSerializer.Serialize(b)),("$l",JsonSerializer.Serialize(l)),("$f",JsonSerializer.Serialize(f)),("$s",s.ToString().ToUpperInvariant()),("$r",rev),("$m",message is null?DBNull.Value:message.Value.ToString("D")),("$at",Text(at)),("$w",a.WorkspaceId),("$id",a.AuditId.ToString("D")));
    private sealed record Receipt(string Workspace,Guid AuditId,string Operation,string Fingerprint,Guid? MessageId);
    private static Receipt? ReadReceipt(SqliteConnection c,SqliteTransaction tx,Guid id){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT workspace_id,audit_id,operation,request_fingerprint,message_id FROM scene_coherence_requests WHERE request_id=$id;";cmd.Parameters.AddWithValue("$id",id.ToString("D"));using var r=cmd.ExecuteReader();return r.Read()?new(r.GetString(0),Guid.Parse(r.GetString(1)),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:Guid.Parse(r.GetString(4))):null;}
    private static void InsertReceipt(SqliteConnection c,SqliteTransaction tx,Guid request,string w,Guid audit,string op,string fingerprint,long rev,Guid? message,DateTimeOffset at)=>Execute(c,tx,"INSERT INTO scene_coherence_requests(request_id,workspace_id,audit_id,operation,request_fingerprint,result_revision,message_id,created_at_utc) VALUES($q,$w,$a,$o,$f,$r,$m,$at);",("$q",request.ToString("D")),("$w",w),("$a",audit.ToString("D")),("$o",op),("$f",fingerprint),("$r",rev),("$m",message is null?DBNull.Value:message.Value.ToString("D")),("$at",Text(at)));
    private static void RequireReceipt(Receipt r,string op,string w,Guid id,string f){if(r.Operation!=op||r.Workspace!=w||r.AuditId!=id||r.Fingerprint!=f)throw new SceneCoherenceConflictException("Request id was reused with different immutable content.");}
    private static void RequireRevision(SceneCoherenceAudit a,long expected){if(a.Revision!=expected)throw new SceneCoherenceConflictException($"Expected revision {expected}, actual {a.Revision}.");}
    private static void ValidateDraft(SceneCoherenceDraft d){if(d.AuditId==Guid.Empty||d.ProjectId==Guid.Empty||d.GenerationId==Guid.Empty||d.SceneApprovalMessageId==Guid.Empty||d.ScenePlanId==Guid.Empty||d.ScenePlanVersion<=0||string.IsNullOrWhiteSpace(d.SceneContentDigest)||string.IsNullOrWhiteSpace(d.SceneKey)||string.IsNullOrWhiteSpace(d.WorkspaceId)||string.IsNullOrWhiteSpace(d.RuleSetVersion)||string.IsNullOrWhiteSpace(d.SourceText)||string.IsNullOrWhiteSpace(d.Actor)||string.IsNullOrWhiteSpace(d.RequestFingerprint))throw new SceneCoherenceValidationException("Complete scene coherence authority and attribution are required.");}
    private static void ValidateRange(int? start,int? length,int textLength){if(start is null&&length is null)return;if(start is null||length is null||start<0||length<=0||start+length>textLength)throw new SceneCoherenceValidationException("Evidence range is outside source text.");}
    private static Guid MessageId(Guid request)=>new(request.ToByteArray().Select((b,i)=>(byte)(b^(i%2==0?0x5A:0xA5))).ToArray());
    private static string Text(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value)=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
    private static void Execute(SqliteConnection c,SqliteTransaction tx,string sql,params (string Name,object Value)[] p){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var x in p)cmd.Parameters.AddWithValue(x.Name,x.Value);cmd.ExecuteNonQuery();}
    private sealed record Authority(string EntryState,string ExitState,IReadOnlyList<string> Beats);
}

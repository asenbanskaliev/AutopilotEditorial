using System.Globalization;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteTransitionAuditStore : ITransitionAuditStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteTransitionAuditStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<TransitionAuditCreateResult> CreateAsync(TransitionAuditDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(d);
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, d.WorkspaceId, d.AuditId);
            if (existing is not null)
            {
                if (existing.ProjectId == d.ProjectId && existing.Scope == d.Scope && existing.Source == d.Source && existing.Target == d.Target && existing.RuleSetVersion == d.RuleSetVersion)
                    return new TransitionAuditCreateResult(existing, true);
                throw new TransitionAuditConflictException("Audit identity already exists with different immutable content.");
            }
            ValidateAuthority(c, tx, d);
            Execute(c, tx, "INSERT INTO transition_audits(workspace_id,audit_id,project_id,scope,source_json,target_json,rule_set_version,assessments_json,findings_json,revision,status,closed_message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$s,$src,$dst,$r,'[]','[]',1,'DRAFT',NULL,$at,$at);",
                ("$w", d.WorkspaceId), ("$id", d.AuditId.ToString("D")), ("$p", d.ProjectId.ToString("D")), ("$s", d.Scope.ToString().ToUpperInvariant()), ("$src", JsonSerializer.Serialize(d.Source)), ("$dst", JsonSerializer.Serialize(d.Target)), ("$r", d.RuleSetVersion), ("$at", Text(at)));
            InsertReceipt(c, tx, d.AuditId, d.WorkspaceId, d.AuditId, "CREATE", d.RequestFingerprint, 1, null, at);
            return new TransitionAuditCreateResult(Require(c, tx, d.WorkspaceId, d.AuditId), false);
        }, ct);
    }

    public ValueTask<TransitionAudit> StartAsync(TransitionAuditControlCommand c, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(c.RequestId,c.WorkspaceId,c.AuditId,"START",c.RequestFingerprint,c.ExpectedRevision,at,(a,x,f)=>
        {
            if(a.Status!=TransitionAuditStatus.Draft) throw new TransitionAuditTransitionException("Only draft audits can start.");
            return (TransitionAuditStatus.Running,x,f,null);
        },ct);

    public ValueTask<TransitionAudit> AssessDimensionAsync(TransitionDimensionCommand c, DateTimeOffset at, CancellationToken ct = default)
    {
        if(string.IsNullOrWhiteSpace(c.Evidence)) throw new TransitionAuditValidationException("Assessment evidence is required.");
        return Mutate(c.RequestId,c.WorkspaceId,c.AuditId,"ASSESS",c.RequestFingerprint,c.ExpectedRevision,at,(a,x,f)=>
        {
            if(a.Status!=TransitionAuditStatus.Running) throw new TransitionAuditTransitionException("Assessments require a running audit.");
            if(x.Any(v=>v.Dimension==c.Dimension)) throw new TransitionAuditConflictException("Dimension already assessed.");
            x.Add(new(c.Dimension,c.Status,c.Evidence,c.Actor,at));
            return (a.Status,x,f,null);
        },ct);
    }

    public ValueTask<TransitionAudit> RecordFindingAsync(TransitionFindingCommand c, DateTimeOffset at, CancellationToken ct = default)
    {
        if(c.FindingId==Guid.Empty||string.IsNullOrWhiteSpace(c.RuleId)||string.IsNullOrWhiteSpace(c.RuleVersion)||string.IsNullOrWhiteSpace(c.Evidence)||string.IsNullOrWhiteSpace(c.Recommendation)) throw new TransitionAuditValidationException("Complete finding attribution is required.");
        return Mutate(c.RequestId,c.WorkspaceId,c.AuditId,"FIND",c.RequestFingerprint,c.ExpectedRevision,at,(a,x,f)=>
        {
            if(a.Status!=TransitionAuditStatus.Running) throw new TransitionAuditTransitionException("Findings require a running audit.");
            if(f.Any(v=>v.FindingId==c.FindingId)) throw new TransitionAuditConflictException("Finding identity already exists.");
            f.Add(new(c.FindingId,c.RuleId,c.RuleVersion,c.Severity,c.Evidence,c.Recommendation,TransitionDecision.Open,null,c.Actor,at,null));
            return (a.Status,x,f,null);
        },ct);
    }

    public ValueTask<TransitionAudit> DecideFindingAsync(TransitionDecisionCommand c, DateTimeOffset at, CancellationToken ct = default)
    {
        if(c.Decision==TransitionDecision.Open||string.IsNullOrWhiteSpace(c.Reason)) throw new TransitionAuditValidationException("Terminal decision and reason are required.");
        return Mutate(c.RequestId,c.WorkspaceId,c.AuditId,"DECIDE",c.RequestFingerprint,c.ExpectedRevision,at,(a,x,f)=>
        {
            if(a.Status is TransitionAuditStatus.Draft or TransitionAuditStatus.Closed) throw new TransitionAuditTransitionException("Finding decisions require an active audit.");
            var i=f.FindIndex(v=>v.FindingId==c.FindingId); if(i<0) throw new KeyNotFoundException("Finding not found.");
            if(f[i].Decision!=TransitionDecision.Open) throw new TransitionAuditConflictException("Finding already decided.");
            f[i]=f[i] with { Decision=c.Decision, DecisionReason=c.Reason, DecidedAtUtc=at };
            return (a.Status,x,f,null);
        },ct);
    }

    public ValueTask<TransitionAudit> ReviewAsync(TransitionAuditControlCommand c, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(c.RequestId,c.WorkspaceId,c.AuditId,"REVIEW",c.RequestFingerprint,c.ExpectedRevision,at,(a,x,f)=>
        {
            if(a.Status!=TransitionAuditStatus.Running) throw new TransitionAuditTransitionException("Only running audits can be reviewed.");
            var required=Enum.GetValues<TransitionDimension>();
            if(required.Any(d=>!x.Any(v=>v.Dimension==d))) throw new TransitionAuditTransitionException("Every transition dimension must be assessed.");
            return (TransitionAuditStatus.Reviewed,x,f,null);
        },ct);

    public ValueTask<TransitionAuditCloseResult> CloseAsync(TransitionAuditCloseCommand c, DateTimeOffset at, CancellationToken ct = default)
    {
        if(string.IsNullOrWhiteSpace(c.Reason)) throw new TransitionAuditValidationException("Close reason is required.");
        return _queue.ExecuteInTransactionAsync((db,tx,token)=>
        {
            token.ThrowIfCancellationRequested();
            var receipt=ReadReceipt(db,tx,c.RequestId);
            if(receipt is not null){RequireReceipt(receipt,"CLOSE",c.WorkspaceId,c.AuditId,c.RequestFingerprint);var replay=Require(db,tx,c.WorkspaceId,c.AuditId);return new TransitionAuditCloseResult(replay,true,receipt.MessageId??throw new InvalidOperationException("Close receipt lacks message id."));}
            var a=Require(db,tx,c.WorkspaceId,c.AuditId); RequireRevision(a,c.ExpectedRevision);
            if(a.Status!=TransitionAuditStatus.Reviewed) throw new TransitionAuditTransitionException("Only reviewed audits can close.");
            if(a.Assessments.Any(x=>x.Status==TransitionAssessmentStatus.Broken) || a.Findings.Any(x=>x.Severity==TransitionSeverity.Blocking&&x.Decision==TransitionDecision.Open)) throw new TransitionAuditTransitionException("Broken dimensions and blocking findings must be resolved before close.");
            var message=MessageId(c.RequestId); Update(db,tx,a,a.Assessments.ToList(),a.Findings.ToList(),TransitionAuditStatus.Closed,a.Revision+1,at,message);
            Execute(db,tx,"INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($m,'editorial.transition-audit.closed','1.0.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);",("$m",message.ToString("D")),("$p",JsonSerializer.Serialize(new{c.WorkspaceId,c.AuditId,a.ProjectId,scope=a.Scope.ToString(),a.Source,a.Target,closedBy=c.Actor,c.Reason})),("$at",Text(at)));
            InsertReceipt(db,tx,c.RequestId,c.WorkspaceId,c.AuditId,"CLOSE",c.RequestFingerprint,a.Revision+1,message,at);
            return new TransitionAuditCloseResult(Require(db,tx,c.WorkspaceId,c.AuditId),false,message);
        },ct);
    }

    public async ValueTask<TransitionAudit?> GetAsync(string workspaceId, Guid auditId, CancellationToken ct = default){ct.ThrowIfCancellationRequested();using var c=_factory.OpenConnection();return await Task.FromResult(Read(c,null,workspaceId,auditId));}
    public async ValueTask DisposeAsync(){if(Interlocked.Exchange(ref _disposed,1)==0)await _queue.DisposeAsync().ConfigureAwait(false);}

    private ValueTask<TransitionAudit> Mutate(Guid requestId,string workspace,Guid auditId,string op,string fingerprint,long expected,DateTimeOffset at,Func<TransitionAudit,List<TransitionDimensionAssessment>,List<TransitionFinding>,(TransitionAuditStatus,List<TransitionDimensionAssessment>,List<TransitionFinding>,Guid?)> mutation,CancellationToken ct)
    {
        if(requestId==Guid.Empty||auditId==Guid.Empty||string.IsNullOrWhiteSpace(workspace)||string.IsNullOrWhiteSpace(fingerprint)) throw new TransitionAuditValidationException("Request identity is required.");
        return _queue.ExecuteInTransactionAsync((c,tx,token)=>
        {
            token.ThrowIfCancellationRequested(); var receipt=ReadReceipt(c,tx,requestId); if(receipt is not null){RequireReceipt(receipt,op,workspace,auditId,fingerprint);return Require(c,tx,workspace,auditId);} var a=Require(c,tx,workspace,auditId); RequireRevision(a,expected); var result=mutation(a,a.Assessments.ToList(),a.Findings.ToList()); Update(c,tx,a,result.Item2,result.Item3,result.Item1,a.Revision+1,at,result.Item4); InsertReceipt(c,tx,requestId,workspace,auditId,op,fingerprint,a.Revision+1,result.Item4,at); return Require(c,tx,workspace,auditId);
        },ct);
    }

    private static void ValidateAuthority(SqliteConnection c,SqliteTransaction tx,TransitionAuditDraft d)
    {
        ValidateEndpoint(c,tx,d.WorkspaceId,d.ProjectId,d.Source);
        ValidateEndpoint(c,tx,d.WorkspaceId,d.ProjectId,d.Target);
        if(d.Source.ArtifactId==d.Target.ArtifactId&&d.Source.Version==d.Target.Version) throw new TransitionAuditValidationException("Source and target must be distinct ordered artifacts.");
    }

    private static void ValidateEndpoint(SqliteConnection c,SqliteTransaction tx,string workspace,Guid projectId,TransitionEndpoint e)
    {
        if(string.IsNullOrWhiteSpace(e.ArtifactType)||e.ArtifactId==Guid.Empty||e.Version<=0||string.IsNullOrWhiteSpace(e.ContentDigest)||string.IsNullOrWhiteSpace(e.StateJson)) throw new TransitionAuditValidationException("Complete transition endpoint authority is required.");
        using var cmd=c.CreateCommand(); cmd.Transaction=tx;
        cmd.CommandText=e.ArtifactType.ToUpperInvariant() switch
        {
            "SCENE"=>"SELECT COUNT(*) FROM scene_coherence_audits WHERE workspace_id=$w AND project_id=$p AND audit_id=$id AND revision=$v AND status='CLOSED' AND scene_content_digest=$d;",
            "PARAGRAPH"=>"SELECT COUNT(*) FROM paragraph_coherence_audits WHERE workspace_id=$w AND project_id=$p AND audit_id=$id AND revision=$v AND status='CLOSED' AND scene_content_digest=$d;",
            _=>throw new TransitionAuditValidationException("Unsupported transition endpoint artifact type.")
        };
        cmd.Parameters.AddWithValue("$w",workspace); cmd.Parameters.AddWithValue("$p",projectId.ToString("D")); cmd.Parameters.AddWithValue("$id",e.ArtifactId.ToString("D")); cmd.Parameters.AddWithValue("$v",e.Version); cmd.Parameters.AddWithValue("$d",e.ContentDigest);
        if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)!=1) throw new TransitionAuditValidationException("Exact closed transition endpoint authority was not found.");
    }

    private static TransitionAudit? Read(SqliteConnection c,SqliteTransaction? tx,string w,Guid id)
    {
        using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="SELECT project_id,scope,source_json,target_json,rule_set_version,assessments_json,findings_json,revision,status,closed_message_id,created_at_utc,updated_at_utc FROM transition_audits WHERE workspace_id=$w AND audit_id=$id;"; cmd.Parameters.AddWithValue("$w",w); cmd.Parameters.AddWithValue("$id",id.ToString("D")); using var r=cmd.ExecuteReader(); if(!r.Read())return null;
        return new(id,Guid.Parse(r.GetString(0)),w,Enum.Parse<TransitionScope>(r.GetString(1),true),JsonSerializer.Deserialize<TransitionEndpoint>(r.GetString(2))!,JsonSerializer.Deserialize<TransitionEndpoint>(r.GetString(3))!,r.GetString(4),JsonSerializer.Deserialize<List<TransitionDimensionAssessment>>(r.GetString(5))??[],JsonSerializer.Deserialize<List<TransitionFinding>>(r.GetString(6))??[],r.GetInt64(7),Enum.Parse<TransitionAuditStatus>(r.GetString(8),true),r.IsDBNull(9)?null:Guid.Parse(r.GetString(9)),Parse(r.GetString(10)),Parse(r.GetString(11)));
    }

    private static TransitionAudit Require(SqliteConnection c,SqliteTransaction tx,string w,Guid id)=>Read(c,tx,w,id)??throw new KeyNotFoundException("Transition audit not found.");
    private static void Update(SqliteConnection c,SqliteTransaction tx,TransitionAudit a,List<TransitionDimensionAssessment>x,List<TransitionFinding>f,TransitionAuditStatus s,long rev,DateTimeOffset at,Guid? message)=>Execute(c,tx,"UPDATE transition_audits SET assessments_json=$x,findings_json=$f,status=$s,revision=$r,closed_message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND audit_id=$id;",("$x",JsonSerializer.Serialize(x)),("$f",JsonSerializer.Serialize(f)),("$s",s.ToString().ToUpperInvariant()),("$r",rev),("$m",message is null?DBNull.Value:message.Value.ToString("D")),("$at",Text(at)),("$w",a.WorkspaceId),("$id",a.AuditId.ToString("D")));
    private sealed record Receipt(string Workspace,Guid AuditId,string Operation,string Fingerprint,Guid? MessageId);
    private static Receipt? ReadReceipt(SqliteConnection c,SqliteTransaction tx,Guid id){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT workspace_id,audit_id,operation,request_fingerprint,message_id FROM transition_audit_requests WHERE request_id=$id;";cmd.Parameters.AddWithValue("$id",id.ToString("D"));using var r=cmd.ExecuteReader();return r.Read()?new(r.GetString(0),Guid.Parse(r.GetString(1)),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:Guid.Parse(r.GetString(4))):null;}
    private static void InsertReceipt(SqliteConnection c,SqliteTransaction tx,Guid request,string w,Guid audit,string op,string fingerprint,long rev,Guid? message,DateTimeOffset at)=>Execute(c,tx,"INSERT INTO transition_audit_requests(request_id,workspace_id,audit_id,operation,request_fingerprint,result_revision,message_id,created_at_utc) VALUES($q,$w,$a,$o,$f,$r,$m,$at);",("$q",request.ToString("D")),("$w",w),("$a",audit.ToString("D")),("$o",op),("$f",fingerprint),("$r",rev),("$m",message is null?DBNull.Value:message.Value.ToString("D")),("$at",Text(at)));
    private static void RequireReceipt(Receipt r,string op,string w,Guid id,string f){if(r.Operation!=op||r.Workspace!=w||r.AuditId!=id||r.Fingerprint!=f)throw new TransitionAuditConflictException("Request id was reused with different immutable content.");}
    private static void RequireRevision(TransitionAudit a,long expected){if(a.Revision!=expected)throw new TransitionAuditConflictException($"Expected revision {expected}, actual {a.Revision}.");}
    private static void ValidateDraft(TransitionAuditDraft d){if(d.AuditId==Guid.Empty||d.ProjectId==Guid.Empty||string.IsNullOrWhiteSpace(d.WorkspaceId)||string.IsNullOrWhiteSpace(d.RuleSetVersion)||string.IsNullOrWhiteSpace(d.Actor)||string.IsNullOrWhiteSpace(d.RequestFingerprint))throw new TransitionAuditValidationException("Complete transition audit identity and attribution are required.");}
    private static Guid MessageId(Guid request)=>new(request.ToByteArray().Select((b,i)=>(byte)(b^(i%2==0?0x3C:0xC3))).ToArray());
    private static string Text(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value)=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
    private static void Execute(SqliteConnection c,SqliteTransaction tx,string sql,params (string Name,object Value)[] p){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var x in p)cmd.Parameters.AddWithValue(x.Name,x.Value);cmd.ExecuteNonQuery();}
}

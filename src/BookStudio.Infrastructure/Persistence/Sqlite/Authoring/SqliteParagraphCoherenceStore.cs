using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteParagraphCoherenceStore : IParagraphCoherenceStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteParagraphCoherenceStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<ParagraphCoherenceCreateResult> CreateAsync(ParagraphCoherenceDraft draft, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(draft);
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, draft.WorkspaceId, draft.AuditId);
            if (existing is not null)
            {
                if (existing.ProjectId == draft.ProjectId && existing.GenerationId == draft.GenerationId && existing.SceneApprovalMessageId == draft.SceneApprovalMessageId && existing.SceneContentDigest == draft.SceneContentDigest && existing.RuleSetVersion == draft.RuleSetVersion && existing.SourceText == draft.SourceText)
                    return new ParagraphCoherenceCreateResult(existing, true);
                throw new ParagraphCoherenceConflictException("Audit identity already exists with different immutable content.");
            }
            RequireApprovedScene(c, tx, draft);
            var paragraphs = Segment(draft.SourceText);
            Execute(c, tx, "INSERT INTO paragraph_coherence_audits(workspace_id,audit_id,project_id,generation_id,scene_approval_message_id,scene_content_digest,rule_set_version,source_text,paragraphs_json,findings_json,revision,status,closed_message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$g,$m,$d,$r,$s,$pj,'[]',1,'DRAFT',NULL,$at,$at);",
                ("$w",draft.WorkspaceId),("$id",draft.AuditId.ToString("D")),("$p",draft.ProjectId.ToString("D")),("$g",draft.GenerationId.ToString("D")),("$m",draft.SceneApprovalMessageId.ToString("D")),("$d",draft.SceneContentDigest),("$r",draft.RuleSetVersion),("$s",draft.SourceText),("$pj",JsonSerializer.Serialize(paragraphs)),("$at",Text(at)));
            InsertReceipt(c,tx,CreateRequestId(draft),draft.WorkspaceId,draft.AuditId,"CREATE",draft.RequestFingerprint,1,null,at);
            return new ParagraphCoherenceCreateResult(Require(c,tx,draft.WorkspaceId,draft.AuditId),false);
        },ct);
    }

    public ValueTask<ParagraphCoherenceAudit> StartAsync(ParagraphCoherenceCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.RequestId,command.WorkspaceId,command.AuditId,"START",command.RequestFingerprint,at,command.ExpectedRevision,(current,findings) =>
        {
            if(current.Status!=ParagraphCoherenceStatus.Draft) throw new ParagraphCoherenceTransitionException("Only draft audits can start.");
            return (ParagraphCoherenceStatus.Running,findings,null);
        },ct);

    public ValueTask<ParagraphCoherenceAudit> RecordFindingAsync(ParagraphFindingCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateFinding(command);
        return Mutate(command.RequestId,command.WorkspaceId,command.AuditId,"FIND",command.RequestFingerprint,at,command.ExpectedRevision,(current,findings) =>
        {
            if(current.Status!=ParagraphCoherenceStatus.Running) throw new ParagraphCoherenceTransitionException("Findings can only be recorded while running.");
            if(findings.Any(x=>x.FindingId==command.FindingId)) throw new ParagraphCoherenceConflictException("Finding identity already exists.");
            var paragraph=current.Paragraphs.SingleOrDefault(x=>x.Ordinal==command.ParagraphOrdinal) ?? throw new ParagraphCoherenceValidationException("Paragraph ordinal does not exist.");
            if(command.StartOffset < paragraph.StartOffset || command.StartOffset + command.Length > paragraph.StartOffset + paragraph.Length) throw new ParagraphCoherenceValidationException("Finding range must be inside its paragraph.");
            findings.Add(new(command.FindingId,command.RuleId,command.RuleVersion,command.Category,command.Severity,command.ParagraphOrdinal,command.StartOffset,command.Length,command.Evidence,command.Recommendation,ParagraphFindingDecision.Open,null,command.Actor,at,null));
            return (ParagraphCoherenceStatus.Running,findings,null);
        },ct);
    }

    public ValueTask<ParagraphCoherenceAudit> DecideFindingAsync(ParagraphFindingDecisionCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(command.RequestId,command.WorkspaceId,command.AuditId,command.Actor,command.RequestFingerprint);
        if(command.Decision==ParagraphFindingDecision.Open || string.IsNullOrWhiteSpace(command.Reason)) throw new ParagraphCoherenceValidationException("A terminal finding decision and reason are required.");
        return Mutate(command.RequestId,command.WorkspaceId,command.AuditId,"DECIDE",command.RequestFingerprint,at,command.ExpectedRevision,(current,findings) =>
        {
            if(current.Status is ParagraphCoherenceStatus.Draft or ParagraphCoherenceStatus.Closed) throw new ParagraphCoherenceTransitionException("Finding decisions require an active audit.");
            var index=findings.FindIndex(x=>x.FindingId==command.FindingId);
            if(index<0) throw new KeyNotFoundException("Finding was not found.");
            if(findings[index].Decision!=ParagraphFindingDecision.Open) throw new ParagraphCoherenceConflictException("Finding is already decided.");
            findings[index]=findings[index] with { Decision=command.Decision,DecisionReason=command.Reason,DecidedAtUtc=at };
            return (current.Status,findings,null);
        },ct);
    }

    public ValueTask<ParagraphCoherenceAudit> ReviewAsync(ParagraphCoherenceCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.RequestId,command.WorkspaceId,command.AuditId,"REVIEW",command.RequestFingerprint,at,command.ExpectedRevision,(current,findings) =>
        {
            if(current.Status!=ParagraphCoherenceStatus.Running) throw new ParagraphCoherenceTransitionException("Only running audits can be reviewed.");
            return (ParagraphCoherenceStatus.Reviewed,findings,null);
        },ct);

    public ValueTask<ParagraphCoherenceCloseResult> CloseAsync(ParagraphCoherenceCloseCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(command.RequestId,command.WorkspaceId,command.AuditId,command.Actor,command.RequestFingerprint);
        if(string.IsNullOrWhiteSpace(command.Reason)) throw new ParagraphCoherenceValidationException("Close reason is required.");
        return _queue.ExecuteInTransactionAsync((c,tx,token) =>
        {
            token.ThrowIfCancellationRequested();
            var receipt=ReadReceipt(c,tx,command.RequestId);
            if(receipt is not null)
            {
                RequireReceipt(receipt,"CLOSE",command.WorkspaceId,command.AuditId,command.RequestFingerprint);
                var replay=Require(c,tx,command.WorkspaceId,command.AuditId);
                return new ParagraphCoherenceCloseResult(replay,true,receipt.MessageId ?? throw new InvalidOperationException("Close receipt lacks message id."));
            }
            var current=Require(c,tx,command.WorkspaceId,command.AuditId);
            RequireRevision(current,command.ExpectedRevision);
            if(current.Status!=ParagraphCoherenceStatus.Reviewed) throw new ParagraphCoherenceTransitionException("Only reviewed audits can close.");
            if(current.Findings.Any(x=>x.Severity==ParagraphFindingSeverity.Blocking && x.Decision==ParagraphFindingDecision.Open)) throw new ParagraphCoherenceTransitionException("Blocking findings must be decided before close.");
            var messageId=MessageId(command.RequestId);
            Update(c,tx,current,current.Findings.ToList(),ParagraphCoherenceStatus.Closed,current.Revision+1,at,messageId);
            Execute(c,tx,"INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($m,'editorial.paragraph-coherence.closed','1.0.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);",
                ("$m",messageId.ToString("D")),("$p",JsonSerializer.Serialize(new { command.WorkspaceId,command.AuditId,current.ProjectId,current.GenerationId,current.SceneContentDigest,findings=current.Findings.Count,closedBy=command.Actor,command.Reason })),("$at",Text(at)));
            InsertReceipt(c,tx,command.RequestId,command.WorkspaceId,command.AuditId,"CLOSE",command.RequestFingerprint,current.Revision+1,messageId,at);
            return new ParagraphCoherenceCloseResult(Require(c,tx,command.WorkspaceId,command.AuditId),false,messageId);
        },ct);
    }

    public async ValueTask<ParagraphCoherenceAudit?> GetAsync(string workspaceId, Guid auditId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var c=_factory.OpenConnection(); return await Task.FromResult(Read(c,null,workspaceId,auditId)); }

    public async ValueTask DisposeAsync(){if(Interlocked.Exchange(ref _disposed,1)==0) await _queue.DisposeAsync().ConfigureAwait(false);}

    private ValueTask<ParagraphCoherenceAudit> Mutate(Guid requestId,string workspaceId,Guid auditId,string operation,string fingerprint,DateTimeOffset at,long expectedRevision,Func<ParagraphCoherenceAudit,List<ParagraphFinding>,(ParagraphCoherenceStatus Status,List<ParagraphFinding> Findings,Guid? Message)> mutation,CancellationToken ct)
    {
        ValidateRequest(requestId,workspaceId,auditId,"actor",fingerprint,allowSyntheticActor:true);
        return _queue.ExecuteInTransactionAsync((c,tx,token)=>
        {
            token.ThrowIfCancellationRequested();
            var receipt=ReadReceipt(c,tx,requestId);
            if(receipt is not null){RequireReceipt(receipt,operation,workspaceId,auditId,fingerprint);return Require(c,tx,workspaceId,auditId);}
            var current=Require(c,tx,workspaceId,auditId);RequireRevision(current,expectedRevision);
            var result=mutation(current,current.Findings.ToList());
            Update(c,tx,current,result.Findings,result.Status,current.Revision+1,at,result.Message);
            InsertReceipt(c,tx,requestId,workspaceId,auditId,operation,fingerprint,current.Revision+1,result.Message,at);
            return Require(c,tx,workspaceId,auditId);
        },ct);
    }

    private static ParagraphCoherenceAudit? Read(SqliteConnection c,SqliteTransaction? tx,string workspaceId,Guid auditId)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT project_id,generation_id,scene_approval_message_id,scene_content_digest,rule_set_version,source_text,paragraphs_json,findings_json,revision,status,closed_message_id,created_at_utc,updated_at_utc FROM paragraph_coherence_audits WHERE workspace_id=$w AND audit_id=$id;";cmd.Parameters.AddWithValue("$w",workspaceId);cmd.Parameters.AddWithValue("$id",auditId.ToString("D"));using var r=cmd.ExecuteReader();if(!r.Read())return null;
        return new(auditId,Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),Guid.Parse(r.GetString(2)),r.GetString(3),workspaceId,r.GetString(4),r.GetString(5),JsonSerializer.Deserialize<IReadOnlyList<ParagraphSegment>>(r.GetString(6))??Array.Empty<ParagraphSegment>(),JsonSerializer.Deserialize<IReadOnlyList<ParagraphFinding>>(r.GetString(7))??Array.Empty<ParagraphFinding>(),r.GetInt64(8),Enum.Parse<ParagraphCoherenceStatus>(r.GetString(9),true),r.IsDBNull(10)?null:Guid.Parse(r.GetString(10)),Parse(r.GetString(11)),Parse(r.GetString(12)));
    }

    private static void Update(SqliteConnection c,SqliteTransaction tx,ParagraphCoherenceAudit current,List<ParagraphFinding> findings,ParagraphCoherenceStatus status,long revision,DateTimeOffset at,Guid? message)
    {Execute(c,tx,"UPDATE paragraph_coherence_audits SET findings_json=$f,revision=$r,status=$s,closed_message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND audit_id=$id;",("$f",JsonSerializer.Serialize(findings)),("$r",revision),("$s",status.ToString().ToUpperInvariant()),("$m",message is null?DBNull.Value:message.Value.ToString("D")),("$at",Text(at)),("$w",current.WorkspaceId),("$id",current.AuditId.ToString("D")));}

    private static IReadOnlyList<ParagraphSegment> Segment(string text)
    {
        var result=new List<ParagraphSegment>();var index=0;var ordinal=1;
        while(index<text.Length){while(index<text.Length && (text[index]=='\r'||text[index]=='\n')) index++;if(index>=text.Length)break;var start=index;while(index<text.Length && !(text[index]=='\n' && index+1<text.Length && text[index+1]=='\n')) index++;var end=index;while(end>start && (text[end-1]=='\r'||text[end-1]=='\n'))end--;var value=text[start..end];if(!string.IsNullOrWhiteSpace(value))result.Add(new(ordinal++,start,end-start,value));}
        if(result.Count==0)throw new ParagraphCoherenceValidationException("Source text must contain at least one paragraph.");return result;
    }

    private static void RequireApprovedScene(SqliteConnection c,SqliteTransaction tx,ParagraphCoherenceDraft d)
    {using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT COUNT(*) FROM scene_generations g JOIN scene_generation_attempts a ON a.workspace_id=g.workspace_id AND a.generation_id=g.generation_id WHERE g.workspace_id=$w AND g.generation_id=$g AND g.project_id=$p AND g.status='APPROVED' AND g.approval_message_id=$m AND a.status='GENERATED' AND a.content_digest=$d AND a.generated_text=$t;";cmd.Parameters.AddWithValue("$w",d.WorkspaceId);cmd.Parameters.AddWithValue("$g",d.GenerationId.ToString("D"));cmd.Parameters.AddWithValue("$p",d.ProjectId.ToString("D"));cmd.Parameters.AddWithValue("$m",d.SceneApprovalMessageId.ToString("D"));cmd.Parameters.AddWithValue("$d",d.SceneContentDigest);cmd.Parameters.AddWithValue("$t",d.SourceText);if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)!=1)throw new ParagraphCoherenceValidationException("Exact approved scene authority was not found.");}

    private sealed record Receipt(string WorkspaceId,Guid AuditId,string Operation,string Fingerprint,Guid? MessageId);
    private static Receipt? ReadReceipt(SqliteConnection c,SqliteTransaction tx,Guid id){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT workspace_id,audit_id,operation,request_fingerprint,message_id FROM paragraph_coherence_requests WHERE request_id=$id;";cmd.Parameters.AddWithValue("$id",id.ToString("D"));using var r=cmd.ExecuteReader();return r.Read()?new(r.GetString(0),Guid.Parse(r.GetString(1)),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:Guid.Parse(r.GetString(4))):null;}
    private static void InsertReceipt(SqliteConnection c,SqliteTransaction tx,Guid requestId,string w,Guid id,string op,string fingerprint,long revision,Guid? message,DateTimeOffset at)=>Execute(c,tx,"INSERT INTO paragraph_coherence_requests(request_id,workspace_id,audit_id,operation,request_fingerprint,result_revision,message_id,created_at_utc) VALUES($r,$w,$id,$op,$f,$rev,$m,$at);",("$r",requestId.ToString("D")),("$w",w),("$id",id.ToString("D")),("$op",op),("$f",fingerprint),("$rev",revision),("$m",message is null?DBNull.Value:message.Value.ToString("D")),("$at",Text(at)));
    private static void RequireReceipt(Receipt r,string op,string w,Guid id,string fingerprint){if(r.Operation!=op||r.WorkspaceId!=w||r.AuditId!=id||r.Fingerprint!=fingerprint)throw new ParagraphCoherenceConflictException("Request id was reused with different immutable content.");}
    private static ParagraphCoherenceAudit Require(SqliteConnection c,SqliteTransaction tx,string w,Guid id)=>Read(c,tx,w,id)??throw new KeyNotFoundException("Paragraph coherence audit was not found.");
    private static void RequireRevision(ParagraphCoherenceAudit a,long expected){if(a.Revision!=expected)throw new ParagraphCoherenceConflictException("Expected audit revision is stale.");}
    private static void ValidateDraft(ParagraphCoherenceDraft d){if(d is null||d.AuditId==Guid.Empty||d.ProjectId==Guid.Empty||d.GenerationId==Guid.Empty||d.SceneApprovalMessageId==Guid.Empty||string.IsNullOrWhiteSpace(d.SceneContentDigest)||string.IsNullOrWhiteSpace(d.WorkspaceId)||string.IsNullOrWhiteSpace(d.RuleSetVersion)||string.IsNullOrWhiteSpace(d.SourceText)||string.IsNullOrWhiteSpace(d.Actor)||string.IsNullOrWhiteSpace(d.RequestFingerprint))throw new ParagraphCoherenceValidationException("Audit draft is incomplete.");}
    private static void ValidateFinding(ParagraphFindingCommand c){ValidateRequest(c.RequestId,c.WorkspaceId,c.AuditId,c.Actor,c.RequestFingerprint);if(c.FindingId==Guid.Empty||string.IsNullOrWhiteSpace(c.RuleId)||string.IsNullOrWhiteSpace(c.RuleVersion)||c.ParagraphOrdinal<=0||c.StartOffset<0||c.Length<=0||string.IsNullOrWhiteSpace(c.Evidence)||string.IsNullOrWhiteSpace(c.Recommendation))throw new ParagraphCoherenceValidationException("Finding is incomplete.");}
    private static void ValidateRequest(Guid request,string w,Guid id,string actor,string fingerprint,bool allowSyntheticActor=false){if(request==Guid.Empty||id==Guid.Empty||string.IsNullOrWhiteSpace(w)||(!allowSyntheticActor&&string.IsNullOrWhiteSpace(actor))||string.IsNullOrWhiteSpace(fingerprint))throw new ParagraphCoherenceValidationException("Audit request is incomplete.");}
    private static Guid CreateRequestId(ParagraphCoherenceDraft d)=>new(SHA256.HashData(Encoding.UTF8.GetBytes("paragraph-coherence-create:"+d.WorkspaceId+":"+d.AuditId.ToString("D")))[..16]);
    private static Guid MessageId(Guid request)=>new(SHA256.HashData(Encoding.UTF8.GetBytes("paragraph-coherence-close:"+request.ToString("D")))[..16]);
    private static string Text(DateTimeOffset v)=>v.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);private static DateTimeOffset Parse(string v)=>DateTimeOffset.Parse(v,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
    private static void Execute(SqliteConnection c,SqliteTransaction tx,string sql,params (string Name,object Value)[] values){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var x in values)cmd.Parameters.AddWithValue(x.Name,x.Value);cmd.ExecuteNonQuery();}
}
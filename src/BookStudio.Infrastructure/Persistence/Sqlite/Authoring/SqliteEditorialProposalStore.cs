using System.Globalization;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteEditorialProposalStore : IEditorialProposalStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteEditorialProposalStore(SqliteConnectionFactory factory, int writeQueueCapacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, writeQueueCapacity);
    }

    public ValueTask<EditorialProposalCreateResult> CreateAsync(EditorialProposalDraft draft, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(draft);
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, draft.WorkspaceId, draft.ProposalId);
            if (existing is not null)
            {
                if (existing.ProjectId == draft.ProjectId && existing.DiscoverySessionId == draft.DiscoverySessionId && existing.SchemaVersion == draft.SchemaVersion && Same(existing.Content, draft.Content) && Same(existing.Evidence, draft.Evidence))
                    return new EditorialProposalCreateResult(existing, true);
                throw new EditorialProposalConflictException("Proposal identity already exists with different immutable content.");
            }
            RequireCompletedDiscovery(c, tx, draft.WorkspaceId, draft.DiscoverySessionId, draft.ProjectId);
            Execute(c, tx, "INSERT INTO editorial_proposals(workspace_id,proposal_id,project_id,discovery_session_id,schema_version,status,revision,decision_actor,decision_reason,approval_message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$d,$s,'DRAFT',1,NULL,NULL,NULL,$at,$at);",
                ("$w",draft.WorkspaceId),("$id",draft.ProposalId.ToString("D")),("$p",draft.ProjectId.ToString("D")),("$d",draft.DiscoverySessionId.ToString("D")),("$s",draft.SchemaVersion),("$at",Text(at)));
            InsertRevision(c, tx, draft.WorkspaceId, draft.ProposalId, 1, draft.Content, draft.Evidence, draft.Actor, "initial", draft.RequestFingerprint, at);
            return new EditorialProposalCreateResult(Require(c, tx, draft.WorkspaceId, draft.ProposalId), false);
        }, ct);
    }

    public ValueTask<EditorialProposal> ReviseAsync(EditorialProposalRevisionCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRevision(command);
        return Mutate(command.RequestId, command.WorkspaceId, command.ProposalId, "REVISE", command.RequestFingerprint, at, (c, tx, current) =>
        {
            if (current.Status is EditorialProposalStatus.Approved or EditorialProposalStatus.Submitted) throw new EditorialProposalTransitionException("Only draft or rejected proposals can be revised.");
            RequireRevision(current, command.ExpectedRevision);
            var next = current.Revision + 1;
            InsertRevision(c, tx, command.WorkspaceId, command.ProposalId, next, command.Content, command.Evidence, command.Actor, command.Reason, command.RequestFingerprint, at);
            Execute(c, tx, "UPDATE editorial_proposals SET status='DRAFT',revision=$r,decision_actor=NULL,decision_reason=NULL,approval_message_id=NULL,updated_at_utc=$at WHERE workspace_id=$w AND proposal_id=$id;",("$r",next),("$at",Text(at)),("$w",command.WorkspaceId),("$id",command.ProposalId.ToString("D")));
        }, ct);
    }

    public ValueTask<EditorialProposal> SubmitAsync(EditorialProposalSubmitCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(command.RequestId, command.WorkspaceId, command.ProposalId, command.Actor, command.RequestFingerprint);
        return Mutate(command.RequestId, command.WorkspaceId, command.ProposalId, "SUBMIT", command.RequestFingerprint, at, (c, tx, current) =>
        {
            if (current.Status != EditorialProposalStatus.Draft) throw new EditorialProposalTransitionException("Only a draft proposal can be submitted.");
            RequireRevision(current, command.ExpectedRevision);
            ValidateContent(current.Content, current.Evidence);
            Execute(c, tx, "UPDATE editorial_proposals SET status='SUBMITTED',updated_at_utc=$at WHERE workspace_id=$w AND proposal_id=$id;",("$at",Text(at)),("$w",command.WorkspaceId),("$id",command.ProposalId.ToString("D")));
        }, ct);
    }

    public ValueTask<EditorialProposalDecisionResult> DecideAsync(EditorialProposalDecisionCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(command.RequestId, command.WorkspaceId, command.ProposalId, command.Actor, command.RequestFingerprint);
        if (string.IsNullOrWhiteSpace(command.Reason)) throw new EditorialProposalValidationException("Decision reason is required.");
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var op = command.Decision == EditorialProposalDecision.Approve ? "APPROVE" : "REJECT";
            var prior = ReadRequest(c, tx, command.RequestId);
            if (prior is not null)
            {
                RequireRequest(prior, op, command.WorkspaceId, command.ProposalId, command.RequestFingerprint);
                var replay = Require(c, tx, command.WorkspaceId, command.ProposalId);
                return new EditorialProposalDecisionResult(replay, true, replay.ApprovalMessageId);
            }
            var current = Require(c, tx, command.WorkspaceId, command.ProposalId);
            if (current.Status != EditorialProposalStatus.Submitted) throw new EditorialProposalTransitionException("Only a submitted proposal can be decided.");
            RequireRevision(current, command.ExpectedRevision);
            Guid? messageId = null;
            if (command.Decision == EditorialProposalDecision.Approve)
            {
                messageId = DeterministicMessageId(command.RequestId);
                Execute(c, tx, "UPDATE editorial_proposals SET status='APPROVED',decision_actor=$a,decision_reason=$r,approval_message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND proposal_id=$id;",("$a",command.Actor),("$r",command.Reason),("$m",messageId.Value.ToString("D")),("$at",Text(at)),("$w",command.WorkspaceId),("$id",command.ProposalId.ToString("D")));
                Execute(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($m,'editorial.proposal.approved','1.0.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);",("$m",messageId.Value.ToString("D")),("$p",JsonSerializer.Serialize(new { command.WorkspaceId, command.ProposalId, current.ProjectId, current.DiscoverySessionId, revision=current.Revision, approvedBy=command.Actor })),("$at",Text(at)));
            }
            else
            {
                Execute(c, tx, "UPDATE editorial_proposals SET status='REJECTED',decision_actor=$a,decision_reason=$r,approval_message_id=NULL,updated_at_utc=$at WHERE workspace_id=$w AND proposal_id=$id;",("$a",command.Actor),("$r",command.Reason),("$at",Text(at)),("$w",command.WorkspaceId),("$id",command.ProposalId.ToString("D")));
            }
            InsertRequest(c, tx, command.RequestId, command.WorkspaceId, command.ProposalId, op, command.RequestFingerprint, current.Revision, messageId, at);
            return new EditorialProposalDecisionResult(Require(c, tx, command.WorkspaceId, command.ProposalId), false, messageId);
        }, ct);
    }

    public async ValueTask<EditorialProposal?> GetAsync(string workspaceId, Guid proposalId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var c=_factory.OpenConnection(); return await Task.FromResult(Read(c,null,workspaceId,proposalId)).ConfigureAwait(false); }
    public async ValueTask DisposeAsync(){ if(Interlocked.Exchange(ref _disposed,1)==0) await _queue.DisposeAsync().ConfigureAwait(false); }

    private ValueTask<EditorialProposal> Mutate(Guid requestId,string workspaceId,Guid proposalId,string op,string fingerprint,DateTimeOffset at,Action<SqliteConnection,SqliteTransaction,EditorialProposal> action,CancellationToken ct) =>
        _queue.ExecuteInTransactionAsync((c,tx,token)=>{ token.ThrowIfCancellationRequested(); var prior=ReadRequest(c,tx,requestId); if(prior is not null){RequireRequest(prior,op,workspaceId,proposalId,fingerprint); return Require(c,tx,workspaceId,proposalId);} var current=Require(c,tx,workspaceId,proposalId); action(c,tx,current); var updated=Require(c,tx,workspaceId,proposalId); InsertRequest(c,tx,requestId,workspaceId,proposalId,op,fingerprint,updated.Revision,updated.ApprovalMessageId,at); return updated; },ct);

    private sealed record RequestRow(string WorkspaceId,Guid ProposalId,string Operation,string Fingerprint);
    private static EditorialProposal Require(SqliteConnection c,SqliteTransaction tx,string w,Guid id)=>Read(c,tx,w,id)??throw new KeyNotFoundException("Editorial proposal was not found.");
    private static EditorialProposal? Read(SqliteConnection c,SqliteTransaction? tx,string w,Guid id)
    {
        using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="SELECT project_id,discovery_session_id,schema_version,status,revision,decision_actor,decision_reason,approval_message_id,created_at_utc,updated_at_utc FROM editorial_proposals WHERE workspace_id=$w AND proposal_id=$id;"; cmd.Parameters.AddWithValue("$w",w); cmd.Parameters.AddWithValue("$id",id.ToString("D")); using var r=cmd.ExecuteReader(); if(!r.Read()) return null;
        var project=Guid.Parse(r.GetString(0)); var discovery=Guid.Parse(r.GetString(1)); var schema=r.GetString(2); var status=Enum.Parse<EditorialProposalStatus>(r.GetString(3),true); var revision=r.GetInt64(4); var actor=r.IsDBNull(5)?null:r.GetString(5); var reason=r.IsDBNull(6)?null:r.GetString(6); Guid? message=r.IsDBNull(7)?null:Guid.Parse(r.GetString(7)); var created=ParseTime(r.GetString(8)); var updated=ParseTime(r.GetString(9)); r.Close();
        using var rev=c.CreateCommand(); rev.Transaction=tx; rev.CommandText="SELECT content_json,evidence_json FROM editorial_proposal_revisions WHERE workspace_id=$w AND proposal_id=$id AND revision=$r;"; rev.Parameters.AddWithValue("$w",w); rev.Parameters.AddWithValue("$id",id.ToString("D")); rev.Parameters.AddWithValue("$r",revision); using var rr=rev.ExecuteReader(); if(!rr.Read()) throw new InvalidOperationException("Proposal revision is missing.");
        var content=JsonSerializer.Deserialize<EditorialProposalContent>(rr.GetString(0))??throw new InvalidOperationException("Proposal content is invalid."); var evidence=JsonSerializer.Deserialize<List<ProposalEvidenceReference>>(rr.GetString(1))??[];
        return new(id,project,discovery,w,schema,status,revision,content,evidence,actor,reason,message,created,updated);
    }
    private static RequestRow? ReadRequest(SqliteConnection c,SqliteTransaction tx,Guid id){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT workspace_id,proposal_id,operation,request_fingerprint FROM editorial_proposal_requests WHERE request_id=$id;";cmd.Parameters.AddWithValue("$id",id.ToString("D"));using var r=cmd.ExecuteReader();return r.Read()?new(r.GetString(0),Guid.Parse(r.GetString(1)),r.GetString(2),r.GetString(3)):null;}
    private static void InsertRequest(SqliteConnection c,SqliteTransaction tx,Guid requestId,string w,Guid id,string op,string fingerprint,long revision,Guid? message,DateTimeOffset at)=>Execute(c,tx,"INSERT INTO editorial_proposal_requests(request_id,workspace_id,proposal_id,operation,request_fingerprint,result_revision,approval_message_id,created_at_utc) VALUES($r,$w,$id,$op,$f,$v,$m,$at);",("$r",requestId.ToString("D")),("$w",w),("$id",id.ToString("D")),("$op",op),("$f",fingerprint),("$v",revision),("$m",message is null?DBNull.Value:message.Value.ToString("D")),("$at",Text(at)));
    private static void InsertRevision(SqliteConnection c,SqliteTransaction tx,string w,Guid id,long revision,EditorialProposalContent content,IReadOnlyList<ProposalEvidenceReference> evidence,string actor,string reason,string fingerprint,DateTimeOffset at)=>Execute(c,tx,"INSERT INTO editorial_proposal_revisions(workspace_id,proposal_id,revision,content_json,evidence_json,actor,reason,content_fingerprint,created_at_utc) VALUES($w,$id,$r,$c,$e,$a,$reason,$f,$at);",("$w",w),("$id",id.ToString("D")),("$r",revision),("$c",JsonSerializer.Serialize(content)),("$e",JsonSerializer.Serialize(evidence)),("$a",actor),("$reason",reason),("$f",fingerprint),("$at",Text(at)));
    private static void RequireCompletedDiscovery(SqliteConnection c,SqliteTransaction tx,string w,Guid session,Guid project){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT COUNT(*) FROM discovery_sessions WHERE workspace_id=$w AND session_id=$s AND project_id=$p AND status='COMPLETED';";cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$s",session.ToString("D"));cmd.Parameters.AddWithValue("$p",project.ToString("D"));if(Convert.ToInt32(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)!=1)throw new EditorialProposalValidationException("A completed discovery session for the project is required.");}
    private static void RequireRequest(RequestRow r,string op,string w,Guid id,string fingerprint){if(r.Operation!=op||r.WorkspaceId!=w||r.ProposalId!=id||r.Fingerprint!=fingerprint)throw new EditorialProposalConflictException("Request ID was reused with different immutable content.");}
    private static void RequireRevision(EditorialProposal p,long expected){if(p.Revision!=expected)throw new EditorialProposalConflictException($"Expected revision {expected}, current revision is {p.Revision}.");}
    private static void ValidateDraft(EditorialProposalDraft d){if(d is null||d.ProposalId==Guid.Empty||d.ProjectId==Guid.Empty||d.DiscoverySessionId==Guid.Empty||string.IsNullOrWhiteSpace(d.WorkspaceId)||string.IsNullOrWhiteSpace(d.SchemaVersion)||string.IsNullOrWhiteSpace(d.Actor)||string.IsNullOrWhiteSpace(d.RequestFingerprint))throw new EditorialProposalValidationException("Proposal draft is invalid.");ValidateContent(d.Content,d.Evidence);}
    private static void ValidateRevision(EditorialProposalRevisionCommand c){ValidateRequest(c.RequestId,c.WorkspaceId,c.ProposalId,c.Actor,c.RequestFingerprint);if(c.ExpectedRevision<=0||string.IsNullOrWhiteSpace(c.Reason))throw new EditorialProposalValidationException("Proposal revision is invalid.");ValidateContent(c.Content,c.Evidence);}
    private static void ValidateRequest(Guid requestId,string w,Guid id,string actor,string fingerprint){if(requestId==Guid.Empty||id==Guid.Empty||string.IsNullOrWhiteSpace(w)||string.IsNullOrWhiteSpace(actor)||string.IsNullOrWhiteSpace(fingerprint))throw new EditorialProposalValidationException("Proposal request is invalid.");}
    private static void ValidateContent(EditorialProposalContent c,IReadOnlyList<ProposalEvidenceReference> e){if(c is null||new[]{c.Premise,c.Audience,c.Promise,c.Scope,c.Differentiators,c.Risks,c.Assumptions,c.SuccessCriteria,c.RecommendedNextStep}.Any(string.IsNullOrWhiteSpace)||e is null||e.Count==0||e.Any(x=>string.IsNullOrWhiteSpace(x.Kind)||string.IsNullOrWhiteSpace(x.Key)||string.IsNullOrWhiteSpace(x.Reference)))throw new EditorialProposalValidationException("Proposal content and evidence must be complete.");}
    private static bool Same(EditorialProposalContent a,EditorialProposalContent b)=>a==b;
    private static bool Same(IReadOnlyList<ProposalEvidenceReference> a,IReadOnlyList<ProposalEvidenceReference> b)=>JsonSerializer.Serialize(a)==JsonSerializer.Serialize(b);
    private static Guid DeterministicMessageId(Guid id){var b=id.ToByteArray();b[0]^=0x52;b[15]^=0xA5;return new Guid(b);}
    private static void Execute(SqliteConnection c,SqliteTransaction tx,string sql,params (string Name,object? Value)[] values){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var v in values)cmd.Parameters.AddWithValue(v.Name,v.Value??DBNull.Value);cmd.ExecuteNonQuery();}
    private static string Text(DateTimeOffset v)=>v.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string v)=>DateTimeOffset.Parse(v,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
}

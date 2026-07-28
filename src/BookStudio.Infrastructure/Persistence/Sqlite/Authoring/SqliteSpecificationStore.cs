using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteSpecificationStore : ISpecificationStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteSpecificationStore(SqliteConnectionFactory factory, int writeQueueCapacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, writeQueueCapacity);
    }

    public ValueTask<SpecificationCreateResult> CreateAsync(SpecificationDraft draft, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(draft);
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, draft.WorkspaceId, draft.SpecificationId);
            if (existing is not null)
            {
                if (existing.ProjectId == draft.ProjectId && existing.ProposalId == draft.ProposalId && existing.ProposalRevision == draft.ProposalRevision && existing.ProposalApprovalMessageId == draft.ProposalApprovalMessageId && existing.SchemaVersion == draft.SchemaVersion && existing.Current.Content == draft.Content)
                    return new SpecificationCreateResult(existing, true);
                throw new SpecificationConflictException("Specification identity already exists with different immutable content.");
            }
            RequireApprovedProposal(c, tx, draft);
            Execute(c, tx, "INSERT INTO book_specifications(workspace_id,specification_id,project_id,proposal_id,proposal_revision,proposal_approval_message_id,schema_version,current_version,approval_message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$proposal,$pr,$pm,$s,1,NULL,$at,$at);",
                ("$w",draft.WorkspaceId),("$id",draft.SpecificationId.ToString("D")),("$p",draft.ProjectId.ToString("D")),("$proposal",draft.ProposalId.ToString("D")),("$pr",draft.ProposalRevision),("$pm",draft.ProposalApprovalMessageId.ToString("D")),("$s",draft.SchemaVersion),("$at",Text(at)));
            InsertVersion(c, tx, draft.WorkspaceId, draft.SpecificationId, 1, 1, SpecificationStatus.Draft, draft.Content, null, draft.Actor, "initial", at);
            return new SpecificationCreateResult(Require(c, tx, draft.WorkspaceId, draft.SpecificationId), false);
        }, ct);
    }

    public ValueTask<BookSpecification> ReviseAsync(SpecificationRevisionCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(command.RequestId, command.WorkspaceId, command.SpecificationId, command.Actor, command.RequestFingerprint);
        ValidateContent(command.Content);
        if (string.IsNullOrWhiteSpace(command.Reason)) throw new SpecificationValidationException("Revision reason is required.");
        return Mutate(command.RequestId, command.WorkspaceId, command.SpecificationId, "REVISE", command.RequestFingerprint, at, (c, tx, current) =>
        {
            RequireExpected(current, command.ExpectedVersion, command.ExpectedRevision);
            if (current.Current.Status != SpecificationStatus.Draft) throw new SpecificationTransitionException("Only a draft specification can be revised.");
            InsertVersion(c, tx, command.WorkspaceId, command.SpecificationId, current.CurrentVersion, current.Current.Revision + 1, SpecificationStatus.Draft, command.Content, null, command.Actor, command.Reason, at);
        }, ct);
    }

    public ValueTask<BookSpecification> PrepareAsync(SpecificationControlCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Control(command, "PREPARE", SpecificationStatus.Draft, SpecificationStatus.Prepared, at, ct);

    public ValueTask<BookSpecification> CommitAsync(SpecificationControlCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Control(command, "COMMIT", SpecificationStatus.Prepared, SpecificationStatus.Committed, at, ct);

    public ValueTask<SpecificationApprovalResult> ApproveAsync(SpecificationApprovalCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(command.RequestId, command.WorkspaceId, command.SpecificationId, command.Actor, command.RequestFingerprint);
        if (string.IsNullOrWhiteSpace(command.Reason)) throw new SpecificationValidationException("Approval reason is required.");
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var prior = ReadRequest(c, tx, command.RequestId);
            if (prior is not null)
            {
                RequireRequest(prior, "APPROVE", command.WorkspaceId, command.SpecificationId, command.RequestFingerprint);
                var replay = Require(c, tx, command.WorkspaceId, command.SpecificationId);
                return new SpecificationApprovalResult(replay, true, prior.ApprovalMessageId ?? throw new InvalidOperationException("Approval receipt is missing message identity."));
            }
            var current = Require(c, tx, command.WorkspaceId, command.SpecificationId);
            RequireExpected(current, command.ExpectedVersion, command.ExpectedRevision);
            if (current.Current.Status != SpecificationStatus.Committed) throw new SpecificationTransitionException("Only a committed specification can be approved.");
            var messageId = DeterministicMessageId(command.RequestId);
            var nextRevision = current.Current.Revision + 1;
            InsertVersion(c, tx, command.WorkspaceId, command.SpecificationId, current.CurrentVersion, nextRevision, SpecificationStatus.Approved, current.Current.Content, current.Current.ContentDigest, command.Actor, command.Reason, at);
            Execute(c, tx, "UPDATE book_specifications SET approval_message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND specification_id=$id;",("$m",messageId.ToString("D")),("$at",Text(at)),("$w",command.WorkspaceId),("$id",command.SpecificationId.ToString("D")));
            Execute(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($m,'editorial.specification.approved','1.0.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);",
                ("$m",messageId.ToString("D")),("$p",JsonSerializer.Serialize(new { command.WorkspaceId, command.SpecificationId, current.ProjectId, version=current.CurrentVersion, revision=nextRevision, contentDigest=current.Current.ContentDigest, approvedBy=command.Actor })),("$at",Text(at)));
            InsertRequest(c, tx, command.RequestId, command.WorkspaceId, command.SpecificationId, "APPROVE", command.RequestFingerprint, current.CurrentVersion, nextRevision, messageId, at);
            return new SpecificationApprovalResult(Require(c, tx, command.WorkspaceId, command.SpecificationId), false, messageId);
        }, ct);
    }

    public ValueTask<BookSpecification> OpenNextVersionAsync(SpecificationNextVersionCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(command.RequestId, command.WorkspaceId, command.SpecificationId, command.Actor, command.RequestFingerprint);
        ValidateContent(command.Content);
        if (string.IsNullOrWhiteSpace(command.Reason)) throw new SpecificationValidationException("New-version reason is required.");
        return Mutate(command.RequestId, command.WorkspaceId, command.SpecificationId, "NEXT_VERSION", command.RequestFingerprint, at, (c, tx, current) =>
        {
            if (current.CurrentVersion != command.ExpectedVersion) throw new SpecificationConflictException("Specification version conflict.");
            if (current.Current.Status != SpecificationStatus.Approved) throw new SpecificationTransitionException("A new version can only be opened from an approved version.");
            var next = current.CurrentVersion + 1;
            InsertVersion(c, tx, command.WorkspaceId, command.SpecificationId, next, 1, SpecificationStatus.Draft, command.Content, null, command.Actor, command.Reason, at);
            Execute(c, tx, "UPDATE book_specifications SET current_version=$v,approval_message_id=NULL,updated_at_utc=$at WHERE workspace_id=$w AND specification_id=$id;",("$v",next),("$at",Text(at)),("$w",command.WorkspaceId),("$id",command.SpecificationId.ToString("D")));
        }, ct);
    }

    public async ValueTask<BookSpecification?> GetAsync(string workspaceId, Guid specificationId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var c = _factory.OpenConnection(); return await Task.FromResult(Read(c, null, workspaceId, specificationId)).ConfigureAwait(false); }
    public async ValueTask DisposeAsync(){ if(Interlocked.Exchange(ref _disposed,1)==0) await _queue.DisposeAsync().ConfigureAwait(false); }

    private ValueTask<BookSpecification> Control(SpecificationControlCommand command,string operation,SpecificationStatus expected,SpecificationStatus next,DateTimeOffset at,CancellationToken ct)
    {
        ValidateRequest(command.RequestId,command.WorkspaceId,command.SpecificationId,command.Actor,command.RequestFingerprint);
        return Mutate(command.RequestId,command.WorkspaceId,command.SpecificationId,operation,command.RequestFingerprint,at,(c,tx,current)=>
        {
            RequireExpected(current,command.ExpectedVersion,command.ExpectedRevision);
            if(current.Current.Status!=expected) throw new SpecificationTransitionException($"Specification must be {expected} before {operation.ToLowerInvariant()}.");
            ValidateContent(current.Current.Content);
            var digest=next==SpecificationStatus.Committed?Digest(current.Current.Content):current.Current.ContentDigest;
            InsertVersion(c,tx,command.WorkspaceId,command.SpecificationId,current.CurrentVersion,current.Current.Revision+1,next,current.Current.Content,digest,command.Actor,operation.ToLowerInvariant(),at);
        },ct);
    }

    private ValueTask<BookSpecification> Mutate(Guid requestId,string workspaceId,Guid specificationId,string operation,string fingerprint,DateTimeOffset at,Action<SqliteConnection,SqliteTransaction,BookSpecification> action,CancellationToken ct)=>
        _queue.ExecuteInTransactionAsync((c,tx,token)=>{token.ThrowIfCancellationRequested();var prior=ReadRequest(c,tx,requestId);if(prior is not null){RequireRequest(prior,operation,workspaceId,specificationId,fingerprint);return Require(c,tx,workspaceId,specificationId);}var current=Require(c,tx,workspaceId,specificationId);action(c,tx,current);var updated=Require(c,tx,workspaceId,specificationId);Execute(c,tx,"UPDATE book_specifications SET updated_at_utc=$at WHERE workspace_id=$w AND specification_id=$id;",("$at",Text(at)),("$w",workspaceId),("$id",specificationId.ToString("D")));updated=Require(c,tx,workspaceId,specificationId);InsertRequest(c,tx,requestId,workspaceId,specificationId,operation,fingerprint,updated.CurrentVersion,updated.Current.Revision,updated.ApprovalMessageId,at);return updated;},ct);

    private sealed record RequestRow(string WorkspaceId,Guid SpecificationId,string Operation,string Fingerprint,Guid? ApprovalMessageId);
    private static BookSpecification Require(SqliteConnection c,SqliteTransaction tx,string w,Guid id)=>Read(c,tx,w,id)??throw new KeyNotFoundException("Book specification was not found.");
    private static BookSpecification? Read(SqliteConnection c,SqliteTransaction? tx,string w,Guid id)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT project_id,proposal_id,proposal_revision,proposal_approval_message_id,schema_version,current_version,approval_message_id,created_at_utc,updated_at_utc FROM book_specifications WHERE workspace_id=$w AND specification_id=$id;";cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$id",id.ToString("D"));using var r=cmd.ExecuteReader();if(!r.Read())return null;
        var project=Guid.Parse(r.GetString(0));var proposal=Guid.Parse(r.GetString(1));var proposalRevision=r.GetInt64(2);var proposalMessage=Guid.Parse(r.GetString(3));var schema=r.GetString(4);var currentVersion=r.GetInt64(5);Guid? approval=r.IsDBNull(6)?null:Guid.Parse(r.GetString(6));var created=ParseTime(r.GetString(7));var updated=ParseTime(r.GetString(8));r.Close();
        using var versions=c.CreateCommand();versions.Transaction=tx;versions.CommandText="SELECT version,revision,status,content_json,content_digest,actor,reason,created_at_utc,updated_at_utc FROM book_specification_versions WHERE workspace_id=$w AND specification_id=$id ORDER BY version,revision;";versions.Parameters.AddWithValue("$w",w);versions.Parameters.AddWithValue("$id",id.ToString("D"));using var vr=versions.ExecuteReader();var latest=new Dictionary<long,SpecificationVersion>();while(vr.Read()){var item=new SpecificationVersion(vr.GetInt64(0),vr.GetInt64(1),Enum.Parse<SpecificationStatus>(vr.GetString(2),true),JsonSerializer.Deserialize<SpecificationContent>(vr.GetString(3))??throw new InvalidOperationException("Invalid specification content."),vr.IsDBNull(4)?null:vr.GetString(4),vr.GetString(5),vr.GetString(6),ParseTime(vr.GetString(7)),ParseTime(vr.GetString(8)));latest[item.Version]=item;}
        return new(id,project,proposal,proposalRevision,proposalMessage,w,schema,currentVersion,latest.Values.OrderBy(x=>x.Version).ToArray(),approval,created,updated);
    }
    private static RequestRow? ReadRequest(SqliteConnection c,SqliteTransaction tx,Guid id){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT workspace_id,specification_id,operation,request_fingerprint,approval_message_id FROM book_specification_requests WHERE request_id=$id;";cmd.Parameters.AddWithValue("$id",id.ToString("D"));using var r=cmd.ExecuteReader();return r.Read()?new(r.GetString(0),Guid.Parse(r.GetString(1)),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:Guid.Parse(r.GetString(4))):null;}
    private static void InsertVersion(SqliteConnection c,SqliteTransaction tx,string w,Guid id,long version,long revision,SpecificationStatus status,SpecificationContent content,string? digest,string actor,string reason,DateTimeOffset at)=>Execute(c,tx,"INSERT INTO book_specification_versions(workspace_id,specification_id,version,revision,status,content_json,content_digest,actor,reason,created_at_utc,updated_at_utc) VALUES($w,$id,$v,$r,$s,$c,$d,$a,$reason,$at,$at);",("$w",w),("$id",id.ToString("D")),("$v",version),("$r",revision),("$s",status.ToString().ToUpperInvariant()),("$c",JsonSerializer.Serialize(content)),("$d",digest is null?DBNull.Value:digest),("$a",actor),("$reason",reason),("$at",Text(at)));
    private static void InsertRequest(SqliteConnection c,SqliteTransaction tx,Guid request,string w,Guid id,string op,string fingerprint,long version,long revision,Guid? message,DateTimeOffset at)=>Execute(c,tx,"INSERT INTO book_specification_requests(request_id,workspace_id,specification_id,operation,request_fingerprint,result_version,result_revision,approval_message_id,created_at_utc) VALUES($r,$w,$id,$op,$f,$v,$rev,$m,$at);",("$r",request.ToString("D")),("$w",w),("$id",id.ToString("D")),("$op",op),("$f",fingerprint),("$v",version),("$rev",revision),("$m",message is null?DBNull.Value:message.Value.ToString("D")),("$at",Text(at)));
    private static void RequireApprovedProposal(SqliteConnection c,SqliteTransaction tx,SpecificationDraft d){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT revision,approval_message_id FROM editorial_proposals WHERE workspace_id=$w AND proposal_id=$p AND project_id=$project AND status='APPROVED';";cmd.Parameters.AddWithValue("$w",d.WorkspaceId);cmd.Parameters.AddWithValue("$p",d.ProposalId.ToString("D"));cmd.Parameters.AddWithValue("$project",d.ProjectId.ToString("D"));using var r=cmd.ExecuteReader();if(!r.Read()||r.GetInt64(0)!=d.ProposalRevision||r.IsDBNull(1)||Guid.Parse(r.GetString(1))!=d.ProposalApprovalMessageId)throw new SpecificationValidationException("An approved proposal with matching revision and approval evidence is required.");}
    private static void RequireRequest(RequestRow r,string op,string w,Guid id,string fingerprint){if(r.Operation!=op||r.WorkspaceId!=w||r.SpecificationId!=id||r.Fingerprint!=fingerprint)throw new SpecificationConflictException("Request ID was reused with different immutable content.");}
    private static void RequireExpected(BookSpecification s,long version,long revision){if(s.CurrentVersion!=version||s.Current.Revision!=revision)throw new SpecificationConflictException("Expected specification version or revision is stale.");}
    private static void ValidateDraft(SpecificationDraft d){if(d is null||d.SpecificationId==Guid.Empty||d.ProjectId==Guid.Empty||d.ProposalId==Guid.Empty||d.ProposalRevision<=0||d.ProposalApprovalMessageId==Guid.Empty||string.IsNullOrWhiteSpace(d.WorkspaceId)||string.IsNullOrWhiteSpace(d.SchemaVersion)||string.IsNullOrWhiteSpace(d.Actor)||string.IsNullOrWhiteSpace(d.RequestFingerprint))throw new SpecificationValidationException("Specification draft is invalid.");ValidateContent(d.Content);}
    private static void ValidateRequest(Guid request,string w,Guid id,string actor,string fingerprint){if(request==Guid.Empty||id==Guid.Empty||string.IsNullOrWhiteSpace(w)||string.IsNullOrWhiteSpace(actor)||string.IsNullOrWhiteSpace(fingerprint))throw new SpecificationValidationException("Specification request is invalid.");}
    private static void ValidateContent(SpecificationContent c){if(c is null||new[]{c.Goals,c.Audience,c.Scope,c.Constraints,c.QualityBars,c.Deliverables,c.AcceptanceCriteria}.Any(string.IsNullOrWhiteSpace))throw new SpecificationValidationException("Specification content is incomplete.");}
    private static string Digest(SpecificationContent c)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(c)))).ToLowerInvariant();
    private static Guid DeterministicMessageId(Guid id){var b=id.ToByteArray();b[4]^=0x53;b[11]^=0xC3;return new Guid(b);}
    private static void Execute(SqliteConnection c,SqliteTransaction tx,string sql,params (string Name,object? Value)[] values){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var value in values)cmd.Parameters.AddWithValue(value.Name,value.Value??DBNull.Value);cmd.ExecuteNonQuery();}
    private static string Text(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string value)=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
}

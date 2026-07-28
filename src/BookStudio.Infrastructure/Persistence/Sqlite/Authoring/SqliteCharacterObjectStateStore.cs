using System.Globalization;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteCharacterObjectStateStore : ICharacterObjectStateStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteCharacterObjectStateStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<NarrativeStateCreateResult> CreateAsync(NarrativeStateDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(d);
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, d.WorkspaceId, d.StateId);
            if (existing is not null)
            {
                var receipt = ReadReceipt(c, tx, d.StateId) ?? throw new NarrativeStateConflictException("Create receipt missing.");
                RequireReceipt(receipt, "CREATE", d.WorkspaceId, d.StateId, d.RequestFingerprint);
                if (!Matches(existing, d)) throw new NarrativeStateConflictException("State identity already exists with different immutable content.");
                return new NarrativeStateCreateResult(existing, true);
            }
            ValidateAuthority(c, tx, d);
            Execute(c, tx, "INSERT INTO narrative_states(workspace_id,state_id,project_id,knowledge_entry_id,transition_audit_id,transition_closed_message_id,entity_kind,entity_key,dimension,value_text,location_text,holder_text,object_type,available,transfers_json,valid_from_utc,valid_to_utc,actor,revision,status,activation_message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$k,$t,$m,$ek,$key,$d,$v,$l,$h,$ot,$a,'[]',$vf,$vt,$actor,1,'DRAFT',NULL,$at,$at);",
                ("$w",d.WorkspaceId),("$id",d.StateId.ToString("D")),("$p",d.ProjectId.ToString("D")),("$k",d.KnowledgeEntryId.ToString("D")),("$t",d.TransitionAuditId.ToString("D")),("$m",d.TransitionClosedMessageId.ToString("D")),("$ek",d.EntityKind.ToString().ToUpperInvariant()),("$key",d.EntityKey),("$d",d.Dimension),("$v",d.Value),("$l",Db(d.Location)),("$h",Db(d.Holder)),("$ot",Db(d.ObjectType)),("$a",d.Available?1:0),("$vf",Text(d.ValidFromUtc)),("$vt",d.ValidToUtc is null?DBNull.Value:Text(d.ValidToUtc.Value)),("$actor",d.Actor),("$at",Text(at)));
            InsertReceipt(c,tx,d.StateId,d.WorkspaceId,d.StateId,"CREATE",d.RequestFingerprint,1,null,at);
            return new NarrativeStateCreateResult(Require(c,tx,d.WorkspaceId,d.StateId),false);
        },ct);
    }

    public ValueTask<NarrativeStateEntry> ActivateAsync(NarrativeStateControlCommand cmd, DateTimeOffset at, CancellationToken ct = default) =>
        _queue.ExecuteInTransactionAsync((c,tx,token) =>
        {
            token.ThrowIfCancellationRequested();
            var receipt=ReadReceipt(c,tx,cmd.RequestId);
            if(receipt is not null){RequireReceipt(receipt,"ACTIVATE",cmd.WorkspaceId,cmd.StateId,cmd.RequestFingerprint);return Require(c,tx,cmd.WorkspaceId,cmd.StateId);}
            var e=Require(c,tx,cmd.WorkspaceId,cmd.StateId);
            if(e.Revision!=cmd.ExpectedRevision) throw new NarrativeStateConflictException("Stale revision.");
            if(e.Status!=NarrativeStateStatus.Draft) throw new NarrativeStateTransitionException("Only draft state can activate.");
            ValidateNoConflict(c,tx,e);
            var message=MessageId(cmd.RequestId);
            Execute(c,tx,"UPDATE narrative_states SET status='ACTIVE',revision=revision+1,activation_message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND state_id=$id;",("$m",message.ToString("D")),("$at",Text(at)),("$w",cmd.WorkspaceId),("$id",cmd.StateId.ToString("D")));
            InsertOutbox(c,tx,message,"editorial.character-object-state.activated",new{cmd.WorkspaceId,cmd.StateId,e.ProjectId,e.EntityKind,e.EntityKey,e.Dimension},at);
            InsertReceipt(c,tx,cmd.RequestId,cmd.WorkspaceId,cmd.StateId,"ACTIVATE",cmd.RequestFingerprint,e.Revision+1,message,at);
            return Require(c,tx,cmd.WorkspaceId,cmd.StateId);
        },ct);

    public ValueTask<NarrativeStateEntry> TransferAsync(ObjectTransferCommand cmd, DateTimeOffset at, CancellationToken ct = default) =>
        _queue.ExecuteInTransactionAsync((c,tx,token) =>
        {
            token.ThrowIfCancellationRequested();
            var receipt=ReadReceipt(c,tx,cmd.RequestId);
            if(receipt is not null){RequireReceipt(receipt,"TRANSFER",cmd.WorkspaceId,cmd.StateId,cmd.RequestFingerprint);return Require(c,tx,cmd.WorkspaceId,cmd.StateId);}
            var e=Require(c,tx,cmd.WorkspaceId,cmd.StateId);
            if(e.Revision!=cmd.ExpectedRevision) throw new NarrativeStateConflictException("Stale revision.");
            if(e.EntityKind!=NarrativeEntityKind.Object||e.Status!=NarrativeStateStatus.Active) throw new NarrativeStateTransitionException("Only active object state can transfer.");
            if(string.IsNullOrWhiteSpace(cmd.ToHolder)||string.IsNullOrWhiteSpace(cmd.ExpectedFromHolder)||!string.Equals(e.Holder,cmd.ExpectedFromHolder,StringComparison.Ordinal)) throw new NarrativeStateConflictException("Transfer origin does not match current holder.");
            var transfers=e.Transfers.ToList();transfers.Add(new(cmd.RequestId,cmd.ExpectedFromHolder,cmd.ToHolder,cmd.ToLocation,cmd.Actor,at));
            var message=MessageId(cmd.RequestId);
            Execute(c,tx,"UPDATE narrative_states SET holder_text=$h,location_text=$l,transfers_json=$x,revision=revision+1,updated_at_utc=$at WHERE workspace_id=$w AND state_id=$id;",("$h",cmd.ToHolder),("$l",Db(cmd.ToLocation)),("$x",JsonSerializer.Serialize(transfers)),("$at",Text(at)),("$w",cmd.WorkspaceId),("$id",cmd.StateId.ToString("D")));
            InsertOutbox(c,tx,message,"editorial.character-object-state.transferred",new{cmd.WorkspaceId,cmd.StateId,from=cmd.ExpectedFromHolder,to=cmd.ToHolder,cmd.ToLocation},at);
            InsertReceipt(c,tx,cmd.RequestId,cmd.WorkspaceId,cmd.StateId,"TRANSFER",cmd.RequestFingerprint,e.Revision+1,message,at);
            return Require(c,tx,cmd.WorkspaceId,cmd.StateId);
        },ct);

    public async ValueTask<NarrativeStateEntry?> GetAsync(string workspaceId, Guid stateId, CancellationToken ct=default){ct.ThrowIfCancellationRequested();using var c=_factory.OpenConnection();return await Task.FromResult(Read(c,null,workspaceId,stateId));}
    public async ValueTask DisposeAsync(){if(Interlocked.Exchange(ref _disposed,1)==0)await _queue.DisposeAsync().ConfigureAwait(false);}

    private static void ValidateAuthority(SqliteConnection c,SqliteTransaction tx,NarrativeStateDraft d){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT 1 FROM knowledge_entries WHERE workspace_id=$w AND entry_id=$k AND project_id=$p AND transition_audit_id=$t AND transition_closed_message_id=$m AND kind='FACT' AND status='ACTIVE';";cmd.Parameters.AddWithValue("$w",d.WorkspaceId);cmd.Parameters.AddWithValue("$k",d.KnowledgeEntryId.ToString("D"));cmd.Parameters.AddWithValue("$p",d.ProjectId.ToString("D"));cmd.Parameters.AddWithValue("$t",d.TransitionAuditId.ToString("D"));cmd.Parameters.AddWithValue("$m",d.TransitionClosedMessageId.ToString("D"));if(cmd.ExecuteScalar() is null)throw new NarrativeStateValidationException("Exact active FACT authority was not found.");}
    private static void ValidateNoConflict(SqliteConnection c,SqliteTransaction tx,NarrativeStateEntry e){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT 1 FROM narrative_states WHERE workspace_id=$w AND project_id=$p AND entity_kind=$ek AND entity_key=$key AND dimension=$d AND status='ACTIVE' AND state_id<>$id AND NOT(valid_to_utc IS NOT NULL AND valid_to_utc <= $vf) AND NOT($vt IS NOT NULL AND valid_from_utc >= $vt);";cmd.Parameters.AddWithValue("$w",e.WorkspaceId);cmd.Parameters.AddWithValue("$p",e.ProjectId.ToString("D"));cmd.Parameters.AddWithValue("$ek",e.EntityKind.ToString().ToUpperInvariant());cmd.Parameters.AddWithValue("$key",e.EntityKey);cmd.Parameters.AddWithValue("$d",e.Dimension);cmd.Parameters.AddWithValue("$id",e.StateId.ToString("D"));cmd.Parameters.AddWithValue("$vf",Text(e.ValidFromUtc));cmd.Parameters.AddWithValue("$vt",e.ValidToUtc is null?DBNull.Value:Text(e.ValidToUtc.Value));if(cmd.ExecuteScalar() is not null)throw new NarrativeStateConflictException("Overlapping active state exists.");}
    private static bool Matches(NarrativeStateEntry e,NarrativeStateDraft d)=>e.ProjectId==d.ProjectId&&e.KnowledgeEntryId==d.KnowledgeEntryId&&e.TransitionAuditId==d.TransitionAuditId&&e.TransitionClosedMessageId==d.TransitionClosedMessageId&&e.EntityKind==d.EntityKind&&e.EntityKey==d.EntityKey&&e.Dimension==d.Dimension&&e.Value==d.Value&&e.Location==d.Location&&e.Holder==d.Holder&&e.ObjectType==d.ObjectType&&e.Available==d.Available&&e.ValidFromUtc==d.ValidFromUtc&&e.ValidToUtc==d.ValidToUtc&&e.Actor==d.Actor;
    private static void ValidateDraft(NarrativeStateDraft d){if(d.StateId==Guid.Empty||d.ProjectId==Guid.Empty||d.KnowledgeEntryId==Guid.Empty||d.TransitionAuditId==Guid.Empty||d.TransitionClosedMessageId==Guid.Empty||string.IsNullOrWhiteSpace(d.WorkspaceId)||string.IsNullOrWhiteSpace(d.EntityKey)||string.IsNullOrWhiteSpace(d.Dimension)||string.IsNullOrWhiteSpace(d.Value)||string.IsNullOrWhiteSpace(d.Actor)||string.IsNullOrWhiteSpace(d.RequestFingerprint)||d.ValidToUtc<=d.ValidFromUtc)throw new NarrativeStateValidationException("Complete valid state is required.");if(d.EntityKind==NarrativeEntityKind.Object&&(string.IsNullOrWhiteSpace(d.ObjectType)||string.IsNullOrWhiteSpace(d.Holder)))throw new NarrativeStateValidationException("Objects require type and holder.");}
    private static NarrativeStateEntry? Read(SqliteConnection c,SqliteTransaction? tx,string w,Guid id){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT project_id,knowledge_entry_id,transition_audit_id,transition_closed_message_id,entity_kind,entity_key,dimension,value_text,location_text,holder_text,object_type,available,transfers_json,valid_from_utc,valid_to_utc,actor,revision,status,activation_message_id,created_at_utc,updated_at_utc FROM narrative_states WHERE workspace_id=$w AND state_id=$id;";cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$id",id.ToString("D"));using var r=cmd.ExecuteReader();if(!r.Read())return null;return new(id,Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),Guid.Parse(r.GetString(2)),Guid.Parse(r.GetString(3)),w,Enum.Parse<NarrativeEntityKind>(r.GetString(4),true),r.GetString(5),r.GetString(6),r.GetString(7),r.IsDBNull(8)?null:r.GetString(8),r.IsDBNull(9)?null:r.GetString(9),r.IsDBNull(10)?null:r.GetString(10),r.GetInt64(11)==1,JsonSerializer.Deserialize<List<ObjectTransfer>>(r.GetString(12))??[],Parse(r.GetString(13)),r.IsDBNull(14)?null:Parse(r.GetString(14)),r.GetString(15),r.GetInt64(16),Enum.Parse<NarrativeStateStatus>(r.GetString(17),true),r.IsDBNull(18)?null:Guid.Parse(r.GetString(18)),Parse(r.GetString(19)),Parse(r.GetString(20)));}
    private static NarrativeStateEntry Require(SqliteConnection c,SqliteTransaction tx,string w,Guid id)=>Read(c,tx,w,id)??throw new KeyNotFoundException("Narrative state not found.");
    private sealed record Receipt(string Workspace,Guid StateId,string Operation,string Fingerprint);
    private static Receipt? ReadReceipt(SqliteConnection c,SqliteTransaction tx,Guid id){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT workspace_id,state_id,operation,request_fingerprint FROM narrative_state_requests WHERE request_id=$id;";cmd.Parameters.AddWithValue("$id",id.ToString("D"));using var r=cmd.ExecuteReader();return r.Read()?new(r.GetString(0),Guid.Parse(r.GetString(1)),r.GetString(2),r.GetString(3)):null;}
    private static void RequireReceipt(Receipt r,string op,string w,Guid id,string fp){if(r.Operation!=op||r.Workspace!=w||r.StateId!=id||r.Fingerprint!=fp)throw new NarrativeStateConflictException("Request id reused with different immutable content.");}
    private static void InsertReceipt(SqliteConnection c,SqliteTransaction tx,Guid q,string w,Guid id,string op,string fp,long rev,Guid? message,DateTimeOffset at)=>Execute(c,tx,"INSERT INTO narrative_state_requests(request_id,workspace_id,state_id,operation,request_fingerprint,result_revision,message_id,created_at_utc) VALUES($q,$w,$id,$o,$f,$r,$m,$at);",("$q",q.ToString("D")),("$w",w),("$id",id.ToString("D")),("$o",op),("$f",fp),("$r",rev),("$m",message is null?DBNull.Value:message.Value.ToString("D")),("$at",Text(at)));
    private static void InsertOutbox(SqliteConnection c,SqliteTransaction tx,Guid message,string type,object payload,DateTimeOffset at)=>Execute(c,tx,"INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($m,$t,'1.0.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);",("$m",message.ToString("D")),("$t",type),("$p",JsonSerializer.Serialize(payload)),("$at",Text(at)));
    private static Guid MessageId(Guid request)=>new(request.ToByteArray().Select((b,i)=>(byte)(b^(i%2==0?0x55:0xAA))).ToArray());
    private static object Db(string? v)=>v is null?DBNull.Value:v;
    private static string Text(DateTimeOffset v)=>v.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);private static DateTimeOffset Parse(string v)=>DateTimeOffset.Parse(v,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
    private static void Execute(SqliteConnection c,SqliteTransaction tx,string sql,params (string Name,object Value)[] p){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var x in p)cmd.Parameters.AddWithValue(x.Name,x.Value);cmd.ExecuteNonQuery();}
}

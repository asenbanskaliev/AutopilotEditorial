using System.Globalization;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteKnowledgeStateStore : IKnowledgeStateStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteKnowledgeStateStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<KnowledgeCreateResult> CreateAsync(KnowledgeDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(d);
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, d.WorkspaceId, d.EntryId);
            if (existing is not null)
            {
                if (existing.ProjectId == d.ProjectId && existing.TransitionAuditId == d.TransitionAuditId && existing.TransitionClosedMessageId == d.TransitionClosedMessageId && existing.Kind == d.Kind && existing.Subject == d.Subject && existing.Object == d.Object && existing.Statement == d.Statement)
                    return new KnowledgeCreateResult(existing, true);
                throw new KnowledgeConflictException("Entry identity already exists with different immutable content.");
            }
            ValidateAuthority(c, tx, d);
            ValidateContradiction(c, tx, d);
            Execute(c, tx, "INSERT INTO knowledge_entries(workspace_id,entry_id,project_id,transition_audit_id,transition_closed_message_id,kind,subject,object_text,statement,evidence,knowners_json,excluded_json,disclosures_json,valid_from_utc,valid_to_utc,revision,status,activation_message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$t,$m,$k,$s,$o,$st,$e,$kn,$ex,'[]',$vf,$vt,1,'DRAFT',NULL,$at,$at);",
                ("$w",d.WorkspaceId),("$id",d.EntryId.ToString("D")),("$p",d.ProjectId.ToString("D")),("$t",d.TransitionAuditId.ToString("D")),("$m",d.TransitionClosedMessageId.ToString("D")),("$k",d.Kind.ToString().ToUpperInvariant()),("$s",d.Subject),("$o",d.Object),("$st",d.Statement),("$e",d.Evidence),("$kn",JsonSerializer.Serialize(Normalize(d.Knowners))),("$ex",JsonSerializer.Serialize(Normalize(d.Excluded))),("$vf",Text(d.ValidFromUtc)),("$vt",d.ValidToUtc is null?DBNull.Value:Text(d.ValidToUtc.Value)),("$at",Text(at)));
            InsertReceipt(c, tx, d.EntryId, d.WorkspaceId, d.EntryId, "CREATE", d.RequestFingerprint, 1, null, at);
            return new KnowledgeCreateResult(Require(c, tx, d.WorkspaceId, d.EntryId), false);
        }, ct);
    }

    public ValueTask<KnowledgeEntry> ActivateAsync(KnowledgeControlCommand c, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(c.RequestId,c.WorkspaceId,c.EntryId,"ACTIVATE",c.RequestFingerprint,c.ExpectedRevision,at,(db,tx,e,k,x,d)=>
        {
            if(e.Status!=KnowledgeStatus.Draft) throw new KnowledgeTransitionException("Only draft knowledge can activate.");
            var message=MessageId(c.RequestId);
            Execute(db,tx,"INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($m,'editorial.knowledge-state.activated','1.0.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);",("$m",message.ToString("D")),("$p",JsonSerializer.Serialize(new{c.WorkspaceId,c.EntryId,e.ProjectId,e.Kind,e.Subject,e.Object})),("$at",Text(at)));
            return (KnowledgeStatus.Active,k,x,d,message);
        },ct);

    public ValueTask<KnowledgeEntry> DiscloseAsync(KnowledgeDisclosureCommand c, DateTimeOffset at, CancellationToken ct = default)
    {
        if(c.AddKnowners.Count==0||string.IsNullOrWhiteSpace(c.Evidence)) throw new KnowledgeValidationException("Disclosure knowners and evidence are required.");
        return Mutate(c.RequestId,c.WorkspaceId,c.EntryId,"DISCLOSE",c.RequestFingerprint,c.ExpectedRevision,at,(db,tx,e,k,x,d)=>
        {
            if(e.Status!=KnowledgeStatus.Active||e.Kind==KnowledgeKind.Fact) throw new KnowledgeTransitionException("Only active beliefs or secrets can be disclosed.");
            var add=Normalize(c.AddKnowners); if(add.Any(v=>x.Contains(v,StringComparer.OrdinalIgnoreCase))) throw new KnowledgeValidationException("Excluded actors cannot receive disclosure.");
            var merged=Normalize(k.Concat(add));
            d.Add(new KnowledgeDisclosure(c.RequestId,add,c.Evidence,c.Actor,at));
            return (e.Status,merged,x,d,e.ActivationMessageId);
        },ct);
    }

    public ValueTask<KnowledgeEntry> SupersedeAsync(KnowledgeTerminalCommand c, DateTimeOffset at, CancellationToken ct = default) => Terminal(c,at,KnowledgeStatus.Superseded,"SUPERSEDE",ct);
    public ValueTask<KnowledgeEntry> RetractAsync(KnowledgeTerminalCommand c, DateTimeOffset at, CancellationToken ct = default) => Terminal(c,at,KnowledgeStatus.Retracted,"RETRACT",ct);

    private ValueTask<KnowledgeEntry> Terminal(KnowledgeTerminalCommand c,DateTimeOffset at,KnowledgeStatus status,string op,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(c.Reason)) throw new KnowledgeValidationException("Terminal reason is required.");
        return Mutate(c.RequestId,c.WorkspaceId,c.EntryId,op,c.RequestFingerprint,c.ExpectedRevision,at,(db,tx,e,k,x,d)=>
        {
            if(e.Status!=KnowledgeStatus.Active) throw new KnowledgeTransitionException("Only active knowledge can terminate.");
            return (status,k,x,d,e.ActivationMessageId);
        },ct);
    }

    public async ValueTask<KnowledgeEntry?> GetAsync(string workspaceId,Guid entryId,CancellationToken ct=default){ct.ThrowIfCancellationRequested();using var c=_factory.OpenConnection();return await Task.FromResult(Read(c,null,workspaceId,entryId));}
    public async ValueTask DisposeAsync(){if(Interlocked.Exchange(ref _disposed,1)==0)await _queue.DisposeAsync().ConfigureAwait(false);}

    private ValueTask<KnowledgeEntry> Mutate(Guid request,string workspace,Guid id,string op,string fingerprint,long expected,DateTimeOffset at,Func<SqliteConnection,SqliteTransaction,KnowledgeEntry,List<string>,List<string>,List<KnowledgeDisclosure>,(KnowledgeStatus,List<string>,List<string>,List<KnowledgeDisclosure>,Guid?)> mutation,CancellationToken ct)
    {
        if(request==Guid.Empty||id==Guid.Empty||string.IsNullOrWhiteSpace(workspace)||string.IsNullOrWhiteSpace(fingerprint)) throw new KnowledgeValidationException("Request identity is required.");
        return _queue.ExecuteInTransactionAsync((c,tx,token)=>
        {
            token.ThrowIfCancellationRequested();var receipt=ReadReceipt(c,tx,request);if(receipt is not null){RequireReceipt(receipt,op,workspace,id,fingerprint);return Require(c,tx,workspace,id);}var e=Require(c,tx,workspace,id);if(e.Revision!=expected)throw new KnowledgeConflictException($"Expected revision {expected}, actual {e.Revision}.");var r=mutation(c,tx,e,e.Knowners.ToList(),e.Excluded.ToList(),e.Disclosures.ToList());Execute(c,tx,"UPDATE knowledge_entries SET knowners_json=$k,excluded_json=$x,disclosures_json=$d,status=$s,revision=$r,activation_message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND entry_id=$id;",("$k",JsonSerializer.Serialize(r.Item2)),("$x",JsonSerializer.Serialize(r.Item3)),("$d",JsonSerializer.Serialize(r.Item4)),("$s",r.Item1.ToString().ToUpperInvariant()),("$r",e.Revision+1),("$m",r.Item5 is null?DBNull.Value:r.Item5.Value.ToString("D")),("$at",Text(at)),("$w",workspace),("$id",id.ToString("D")));InsertReceipt(c,tx,request,workspace,id,op,fingerprint,e.Revision+1,r.Item5,at);return Require(c,tx,workspace,id);
        },ct);
    }

    private static void ValidateAuthority(SqliteConnection c,SqliteTransaction tx,KnowledgeDraft d){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT 1 FROM transition_audits WHERE workspace_id=$w AND audit_id=$a AND project_id=$p AND status='CLOSED' AND closed_message_id=$m;";cmd.Parameters.AddWithValue("$w",d.WorkspaceId);cmd.Parameters.AddWithValue("$a",d.TransitionAuditId.ToString("D"));cmd.Parameters.AddWithValue("$p",d.ProjectId.ToString("D"));cmd.Parameters.AddWithValue("$m",d.TransitionClosedMessageId.ToString("D"));if(cmd.ExecuteScalar() is null)throw new KnowledgeValidationException("Exact closed transition authority was not found.");}
    private static void ValidateContradiction(SqliteConnection c,SqliteTransaction tx,KnowledgeDraft d){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT statement FROM knowledge_entries WHERE workspace_id=$w AND project_id=$p AND subject=$s AND object_text=$o AND status='ACTIVE' AND NOT(valid_to_utc IS NOT NULL AND valid_to_utc <= $vf);";cmd.Parameters.AddWithValue("$w",d.WorkspaceId);cmd.Parameters.AddWithValue("$p",d.ProjectId.ToString("D"));cmd.Parameters.AddWithValue("$s",d.Subject);cmd.Parameters.AddWithValue("$o",d.Object);cmd.Parameters.AddWithValue("$vf",Text(d.ValidFromUtc));using var r=cmd.ExecuteReader();while(r.Read())if(!string.Equals(r.GetString(0),d.Statement,StringComparison.Ordinal))throw new KnowledgeConflictException("Contradictory active knowledge exists for the same subject and object.");}
    private static KnowledgeEntry? Read(SqliteConnection c,SqliteTransaction? tx,string w,Guid id){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT project_id,transition_audit_id,transition_closed_message_id,kind,subject,object_text,statement,evidence,knowners_json,excluded_json,disclosures_json,valid_from_utc,valid_to_utc,revision,status,activation_message_id,created_at_utc,updated_at_utc FROM knowledge_entries WHERE workspace_id=$w AND entry_id=$id;";cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$id",id.ToString("D"));using var r=cmd.ExecuteReader();if(!r.Read())return null;return new(id,Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),Guid.Parse(r.GetString(2)),w,Enum.Parse<KnowledgeKind>(r.GetString(3),true),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),JsonSerializer.Deserialize<List<string>>(r.GetString(8))!,JsonSerializer.Deserialize<List<string>>(r.GetString(9))!,JsonSerializer.Deserialize<List<KnowledgeDisclosure>>(r.GetString(10))!,Parse(r.GetString(11)),r.IsDBNull(12)?null:Parse(r.GetString(12)),r.GetInt64(13),Enum.Parse<KnowledgeStatus>(r.GetString(14),true),r.IsDBNull(15)?null:Guid.Parse(r.GetString(15)),Parse(r.GetString(16)),Parse(r.GetString(17)));}
    private static KnowledgeEntry Require(SqliteConnection c,SqliteTransaction tx,string w,Guid id)=>Read(c,tx,w,id)??throw new KeyNotFoundException("Knowledge entry not found.");
    private sealed record Receipt(string Workspace,Guid EntryId,string Operation,string Fingerprint,Guid? MessageId);
    private static Receipt? ReadReceipt(SqliteConnection c,SqliteTransaction tx,Guid id){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT workspace_id,entry_id,operation,request_fingerprint,message_id FROM knowledge_requests WHERE request_id=$id;";cmd.Parameters.AddWithValue("$id",id.ToString("D"));using var r=cmd.ExecuteReader();return r.Read()?new(r.GetString(0),Guid.Parse(r.GetString(1)),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:Guid.Parse(r.GetString(4))):null;}
    private static void InsertReceipt(SqliteConnection c,SqliteTransaction tx,Guid request,string w,Guid id,string op,string fp,long rev,Guid? message,DateTimeOffset at)=>Execute(c,tx,"INSERT INTO knowledge_requests(request_id,workspace_id,entry_id,operation,request_fingerprint,result_revision,message_id,created_at_utc) VALUES($q,$w,$id,$o,$f,$r,$m,$at);",("$q",request.ToString("D")),("$w",w),("$id",id.ToString("D")),("$o",op),("$f",fp),("$r",rev),("$m",message is null?DBNull.Value:message.Value.ToString("D")),("$at",Text(at)));
    private static void RequireReceipt(Receipt r,string op,string w,Guid id,string fp){if(r.Operation!=op||r.Workspace!=w||r.EntryId!=id||r.Fingerprint!=fp)throw new KnowledgeConflictException("Request id was reused with different immutable content.");}
    private static void ValidateDraft(KnowledgeDraft d){if(d.EntryId==Guid.Empty||d.ProjectId==Guid.Empty||d.TransitionAuditId==Guid.Empty||d.TransitionClosedMessageId==Guid.Empty||string.IsNullOrWhiteSpace(d.WorkspaceId)||string.IsNullOrWhiteSpace(d.Subject)||string.IsNullOrWhiteSpace(d.Object)||string.IsNullOrWhiteSpace(d.Statement)||string.IsNullOrWhiteSpace(d.Evidence)||string.IsNullOrWhiteSpace(d.Actor)||string.IsNullOrWhiteSpace(d.RequestFingerprint)||d.ValidToUtc<=d.ValidFromUtc)throw new KnowledgeValidationException("Complete valid knowledge authority and attribution are required.");var k=Normalize(d.Knowners);var x=Normalize(d.Excluded);if(k.Intersect(x,StringComparer.OrdinalIgnoreCase).Any())throw new KnowledgeValidationException("Knowers and excluded actors must be disjoint.");if(d.Kind==KnowledgeKind.Secret&&k.Count==0)throw new KnowledgeValidationException("Secrets require at least one knower.");}
    private static List<string> Normalize(IEnumerable<string> values)=>values.Where(v=>!string.IsNullOrWhiteSpace(v)).Select(v=>v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v=>v,StringComparer.Ordinal).ToList();
    private static Guid MessageId(Guid request)=>new(request.ToByteArray().Select((b,i)=>(byte)(b^(i%2==0?0x3C:0xC3))).ToArray());
    private static string Text(DateTimeOffset v)=>v.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);private static DateTimeOffset Parse(string v)=>DateTimeOffset.Parse(v,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
    private static void Execute(SqliteConnection c,SqliteTransaction tx,string sql,params (string Name,object Value)[] p){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var x in p)cmd.Parameters.AddWithValue(x.Name,x.Value);cmd.ExecuteNonQuery();}
}

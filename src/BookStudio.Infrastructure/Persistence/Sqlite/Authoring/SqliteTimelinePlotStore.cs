using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteTimelinePlotStore : ITimelinePlotStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteTimelinePlotStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<TimelineEventCreateResult> CreateEventAsync(TimelineEventDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        Validate(d);
        var hash = Hash(d);
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = ReadEvent(c, tx, d.WorkspaceId, d.EventId);
            if (existing is not null)
            {
                RequireReceipt(ReadReceipt(c, tx, d.EventId), "CREATE_EVENT", d.WorkspaceId, d.EventId, d.RequestFingerprint, hash);
                if (!Matches(existing, d)) throw new TimelinePlotConflictException("Event identity already exists with different immutable content.");
                return new TimelineEventCreateResult(existing, true);
            }
            ValidateFactAuthority(c, tx, d.WorkspaceId, d.ProjectId, d.KnowledgeEntryId, d.TransitionAuditId, d.TransitionClosedMessageId, d.OccursAtUtc);
            ValidateDependencies(c, tx, d);
            Execute(c, tx, "INSERT INTO timeline_events(workspace_id,event_id,project_id,knowledge_entry_id,transition_audit_id,transition_closed_message_id,event_key,narrative_order,occurs_at_utc,depends_on_json,summary,actor,revision,status,activation_message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$k,$a,$m,$key,$n,$at,$deps,$s,$actor,1,'DRAFT',NULL,$created,$created);",
                ("$w",d.WorkspaceId),("$id",D(d.EventId)),("$p",D(d.ProjectId)),("$k",D(d.KnowledgeEntryId)),("$a",D(d.TransitionAuditId)),("$m",D(d.TransitionClosedMessageId)),("$key",d.EventKey),("$n",d.NarrativeOrder),("$at",Text(d.OccursAtUtc)),("$deps",JsonSerializer.Serialize(d.DependsOnEventIds)),("$s",d.Summary),("$actor",d.Actor),("$created",Text(at)));
            Receipt(c,tx,d.EventId,d.WorkspaceId,d.EventId,"CREATE_EVENT",d.RequestFingerprint,hash,1,null,at);
            return new TimelineEventCreateResult(RequireEvent(c,tx,d.WorkspaceId,d.EventId),false);
        },ct);
    }

    public ValueTask<TimelineEventEntry> ActivateEventAsync(TimelineEventControl cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        Validate(cmd);
        var hash=Hash(cmd);
        return _queue.ExecuteInTransactionAsync((c,tx,token)=>
        {
            token.ThrowIfCancellationRequested();
            var receipt=ReadReceipt(c,tx,cmd.RequestId);
            if(receipt is not null){RequireReceipt(receipt,"ACTIVATE_EVENT",cmd.WorkspaceId,cmd.EventId,cmd.RequestFingerprint,hash);return RequireEvent(c,tx,cmd.WorkspaceId,cmd.EventId);}
            var e=RequireEvent(c,tx,cmd.WorkspaceId,cmd.EventId);
            if(e.Revision!=cmd.ExpectedRevision)throw new TimelinePlotConflictException("Stale event revision.");
            if(e.Status!=TimelineEventStatus.Draft)throw new TimelinePlotTransitionException("Only draft events can activate.");
            ValidateActiveDependencies(c,tx,e);
            var message=MessageId(cmd.RequestId);
            Execute(c,tx,"UPDATE timeline_events SET status='ACTIVE',revision=revision+1,activation_message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND event_id=$id;",("$m",D(message)),("$at",Text(at)),("$w",cmd.WorkspaceId),("$id",D(cmd.EventId)));
            Outbox(c,tx,message,"editorial.timeline-event.activated",new{cmd.WorkspaceId,cmd.EventId,e.ProjectId,e.EventKey,e.NarrativeOrder,e.OccursAtUtc},at);
            Receipt(c,tx,cmd.RequestId,cmd.WorkspaceId,cmd.EventId,"ACTIVATE_EVENT",cmd.RequestFingerprint,hash,e.Revision+1,message,at);
            return RequireEvent(c,tx,cmd.WorkspaceId,cmd.EventId);
        },ct);
    }

    public ValueTask<PlotThreadCreateResult> CreateThreadAsync(PlotThreadDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        Validate(d);var hash=Hash(d);
        return _queue.ExecuteInTransactionAsync((c,tx,token)=>
        {
            token.ThrowIfCancellationRequested();var existing=ReadThread(c,tx,d.WorkspaceId,d.ThreadId);
            if(existing is not null){RequireReceipt(ReadReceipt(c,tx,d.ThreadId),"CREATE_THREAD",d.WorkspaceId,d.ThreadId,d.RequestFingerprint,hash);if(!Matches(existing,d))throw new TimelinePlotConflictException("Thread identity already exists with different immutable content.");return new(existing,true);}
            ValidateRequiredEvents(c,tx,d.WorkspaceId,d.ProjectId,d.RequiredEventIds,false);
            Execute(c,tx,"INSERT INTO plot_threads(workspace_id,thread_id,project_id,thread_key,title,required_event_ids_json,milestones_json,actor,revision,status,last_message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$key,$t,$events,'[]',$actor,1,'PLANNED',NULL,$at,$at);",("$w",d.WorkspaceId),("$id",D(d.ThreadId)),("$p",D(d.ProjectId)),("$key",d.ThreadKey),("$t",d.Title),("$events",JsonSerializer.Serialize(d.RequiredEventIds)),("$actor",d.Actor),("$at",Text(at)));
            Receipt(c,tx,d.ThreadId,d.WorkspaceId,d.ThreadId,"CREATE_THREAD",d.RequestFingerprint,hash,1,null,at);return new(RequireThread(c,tx,d.WorkspaceId,d.ThreadId),false);
        },ct);
    }

    public ValueTask<PlotThreadEntry> AdvanceThreadAsync(PlotThreadAdvance cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        Validate(cmd);var hash=Hash(cmd);
        return _queue.ExecuteInTransactionAsync((c,tx,token)=>
        {
            token.ThrowIfCancellationRequested();var receipt=ReadReceipt(c,tx,cmd.RequestId);
            if(receipt is not null){RequireReceipt(receipt,"ADVANCE_THREAD",cmd.WorkspaceId,cmd.ThreadId,cmd.RequestFingerprint,hash);return RequireThread(c,tx,cmd.WorkspaceId,cmd.ThreadId);}
            var t=RequireThread(c,tx,cmd.WorkspaceId,cmd.ThreadId);if(t.Revision!=cmd.ExpectedRevision)throw new TimelinePlotConflictException("Stale thread revision.");
            ValidateThreadTransition(t.Status,cmd.TargetStatus);
            if(cmd.TargetStatus==PlotThreadStatus.Resolved)ValidateRequiredEvents(c,tx,t.WorkspaceId,t.ProjectId,t.RequiredEventIds,true);
            if(cmd.MilestoneEventId is Guid eventId)ValidateRequiredEvents(c,tx,t.WorkspaceId,t.ProjectId,new[]{eventId},true);
            var milestones=t.Milestones.ToList();milestones.Add(new(cmd.RequestId,cmd.MilestoneEventId,cmd.TargetStatus,cmd.Reason,cmd.Actor,at));
            var message=MessageId(cmd.RequestId);
            Execute(c,tx,"UPDATE plot_threads SET milestones_json=$x,status=$s,revision=revision+1,last_message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND thread_id=$id;",("$x",JsonSerializer.Serialize(milestones)),("$s",cmd.TargetStatus.ToString().ToUpperInvariant()),("$m",D(message)),("$at",Text(at)),("$w",cmd.WorkspaceId),("$id",D(cmd.ThreadId)));
            Outbox(c,tx,message,cmd.TargetStatus==PlotThreadStatus.Resolved?"editorial.plot-thread.resolved":"editorial.plot-thread.advanced",new{cmd.WorkspaceId,cmd.ThreadId,target=cmd.TargetStatus.ToString(),cmd.MilestoneEventId,cmd.Reason},at);
            Receipt(c,tx,cmd.RequestId,cmd.WorkspaceId,cmd.ThreadId,"ADVANCE_THREAD",cmd.RequestFingerprint,hash,t.Revision+1,message,at);return RequireThread(c,tx,cmd.WorkspaceId,cmd.ThreadId);
        },ct);
    }

    public async ValueTask<TimelineEventEntry?> GetEventAsync(string w,Guid id,CancellationToken ct=default){ct.ThrowIfCancellationRequested();using var c=_factory.OpenConnection();return await Task.FromResult(ReadEvent(c,null,w,id));}
    public async ValueTask<PlotThreadEntry?> GetThreadAsync(string w,Guid id,CancellationToken ct=default){ct.ThrowIfCancellationRequested();using var c=_factory.OpenConnection();return await Task.FromResult(ReadThread(c,null,w,id));}
    public async ValueTask DisposeAsync(){if(Interlocked.Exchange(ref _disposed,1)==0)await _queue.DisposeAsync().ConfigureAwait(false);}

    private static void ValidateFactAuthority(SqliteConnection c,SqliteTransaction tx,string w,Guid p,Guid k,Guid a,Guid m,DateTimeOffset occurs){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT 1 FROM knowledge_entries WHERE workspace_id=$w AND entry_id=$k AND project_id=$p AND transition_audit_id=$a AND transition_closed_message_id=$m AND kind='FACT' AND status='ACTIVE' AND valid_from_utc<=$at AND (valid_to_utc IS NULL OR valid_to_utc>$at);";cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$k",D(k));cmd.Parameters.AddWithValue("$p",D(p));cmd.Parameters.AddWithValue("$a",D(a));cmd.Parameters.AddWithValue("$m",D(m));cmd.Parameters.AddWithValue("$at",Text(occurs));if(cmd.ExecuteScalar() is null)throw new TimelinePlotValidationException("Exact temporally valid FACT authority was not found.");}
    private static void ValidateDependencies(SqliteConnection c,SqliteTransaction tx,TimelineEventDraft d){if(d.DependsOnEventIds.Contains(d.EventId))throw new TimelinePlotValidationException("An event cannot depend on itself.");foreach(var id in d.DependsOnEventIds){var e=ReadEvent(c,tx,d.WorkspaceId,id)??throw new TimelinePlotValidationException("Dependency event not found.");if(e.ProjectId!=d.ProjectId||e.NarrativeOrder>=d.NarrativeOrder||e.OccursAtUtc>d.OccursAtUtc)throw new TimelinePlotConflictException("Dependency violates causal or temporal order.");if(DependsOn(c,tx,d.WorkspaceId,id,d.EventId,new HashSet<Guid>()))throw new TimelinePlotConflictException("Causal cycle detected.");}}
    private static bool DependsOn(SqliteConnection c,SqliteTransaction tx,string w,Guid current,Guid target,HashSet<Guid> seen){if(!seen.Add(current))return false;var e=ReadEvent(c,tx,w,current);if(e is null)return false;if(e.DependsOnEventIds.Contains(target))return true;return e.DependsOnEventIds.Any(x=>DependsOn(c,tx,w,x,target,seen));}
    private static void ValidateActiveDependencies(SqliteConnection c,SqliteTransaction tx,TimelineEventEntry e){foreach(var id in e.DependsOnEventIds){var dependency=ReadEvent(c,tx,e.WorkspaceId,id);if(dependency?.Status!=TimelineEventStatus.Active)throw new TimelinePlotTransitionException("All causal dependencies must be active first.");}}
    private static void ValidateRequiredEvents(SqliteConnection c,SqliteTransaction tx,string w,Guid p,IReadOnlyList<Guid> ids,bool active){foreach(var id in ids.Distinct()){var e=ReadEvent(c,tx,w,id)??throw new TimelinePlotValidationException("Required event not found.");if(e.ProjectId!=p||active&&e.Status!=TimelineEventStatus.Active)throw new TimelinePlotTransitionException("Required event is not an active event in the same project.");}}
    private static void ValidateThreadTransition(PlotThreadStatus from,PlotThreadStatus to){var ok=(from,to) is (PlotThreadStatus.Planned,PlotThreadStatus.Active) or (PlotThreadStatus.Planned,PlotThreadStatus.Abandoned) or (PlotThreadStatus.Active,PlotThreadStatus.Active) or (PlotThreadStatus.Active,PlotThreadStatus.Resolved) or (PlotThreadStatus.Active,PlotThreadStatus.Abandoned);if(!ok)throw new TimelinePlotTransitionException("Invalid plot-thread transition.");}
    private static bool Matches(TimelineEventEntry e,TimelineEventDraft d)=>e.ProjectId==d.ProjectId&&e.KnowledgeEntryId==d.KnowledgeEntryId&&e.TransitionAuditId==d.TransitionAuditId&&e.TransitionClosedMessageId==d.TransitionClosedMessageId&&e.EventKey==d.EventKey&&e.NarrativeOrder==d.NarrativeOrder&&e.OccursAtUtc==d.OccursAtUtc&&e.DependsOnEventIds.SequenceEqual(d.DependsOnEventIds)&&e.Summary==d.Summary&&e.Actor==d.Actor;
    private static bool Matches(PlotThreadEntry e,PlotThreadDraft d)=>e.ProjectId==d.ProjectId&&e.ThreadKey==d.ThreadKey&&e.Title==d.Title&&e.RequiredEventIds.SequenceEqual(d.RequiredEventIds)&&e.Actor==d.Actor;
    private static void Validate(TimelineEventDraft d){if(d.EventId==Guid.Empty||d.ProjectId==Guid.Empty||d.KnowledgeEntryId==Guid.Empty||d.TransitionAuditId==Guid.Empty||d.TransitionClosedMessageId==Guid.Empty||string.IsNullOrWhiteSpace(d.WorkspaceId)||string.IsNullOrWhiteSpace(d.EventKey)||d.NarrativeOrder<0||string.IsNullOrWhiteSpace(d.Summary)||string.IsNullOrWhiteSpace(d.Actor)||string.IsNullOrWhiteSpace(d.RequestFingerprint)||d.DependsOnEventIds.Distinct().Count()!=d.DependsOnEventIds.Count)throw new TimelinePlotValidationException("Complete valid timeline event is required.");}
    private static void Validate(TimelineEventControl c){if(c.RequestId==Guid.Empty||c.EventId==Guid.Empty||string.IsNullOrWhiteSpace(c.WorkspaceId)||string.IsNullOrWhiteSpace(c.Actor)||string.IsNullOrWhiteSpace(c.RequestFingerprint))throw new TimelinePlotValidationException("Complete event command is required.");}
    private static void Validate(PlotThreadDraft d){if(d.ThreadId==Guid.Empty||d.ProjectId==Guid.Empty||string.IsNullOrWhiteSpace(d.WorkspaceId)||string.IsNullOrWhiteSpace(d.ThreadKey)||string.IsNullOrWhiteSpace(d.Title)||string.IsNullOrWhiteSpace(d.Actor)||string.IsNullOrWhiteSpace(d.RequestFingerprint)||d.RequiredEventIds.Distinct().Count()!=d.RequiredEventIds.Count)throw new TimelinePlotValidationException("Complete valid plot thread is required.");}
    private static void Validate(PlotThreadAdvance c){if(c.RequestId==Guid.Empty||c.ThreadId==Guid.Empty||string.IsNullOrWhiteSpace(c.WorkspaceId)||string.IsNullOrWhiteSpace(c.Reason)||string.IsNullOrWhiteSpace(c.Actor)||string.IsNullOrWhiteSpace(c.RequestFingerprint)||c.TargetStatus==PlotThreadStatus.Planned)throw new TimelinePlotValidationException("Complete thread advance is required.");}

    private static TimelineEventEntry? ReadEvent(SqliteConnection c,SqliteTransaction? tx,string w,Guid id){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT project_id,knowledge_entry_id,transition_audit_id,transition_closed_message_id,event_key,narrative_order,occurs_at_utc,depends_on_json,summary,actor,revision,status,activation_message_id,created_at_utc,updated_at_utc FROM timeline_events WHERE workspace_id=$w AND event_id=$id;";cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$id",D(id));using var r=cmd.ExecuteReader();return r.Read()?new(id,Guid.Parse(r.GetString(0)),Guid.Parse(r.GetString(1)),Guid.Parse(r.GetString(2)),Guid.Parse(r.GetString(3)),w,r.GetString(4),r.GetInt64(5),Parse(r.GetString(6)),JsonSerializer.Deserialize<List<Guid>>(r.GetString(7))??[],r.GetString(8),r.GetString(9),r.GetInt64(10),Enum.Parse<TimelineEventStatus>(r.GetString(11),true),r.IsDBNull(12)?null:Guid.Parse(r.GetString(12)),Parse(r.GetString(13)),Parse(r.GetString(14))):null;}
    private static PlotThreadEntry? ReadThread(SqliteConnection c,SqliteTransaction? tx,string w,Guid id){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT project_id,thread_key,title,required_event_ids_json,milestones_json,actor,revision,status,last_message_id,created_at_utc,updated_at_utc FROM plot_threads WHERE workspace_id=$w AND thread_id=$id;";cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$id",D(id));using var r=cmd.ExecuteReader();return r.Read()?new(id,Guid.Parse(r.GetString(0)),w,r.GetString(1),r.GetString(2),JsonSerializer.Deserialize<List<Guid>>(r.GetString(3))??[],JsonSerializer.Deserialize<List<PlotThreadMilestone>>(r.GetString(4))??[],r.GetString(5),r.GetInt64(6),Enum.Parse<PlotThreadStatus>(r.GetString(7),true),r.IsDBNull(8)?null:Guid.Parse(r.GetString(8)),Parse(r.GetString(9)),Parse(r.GetString(10))):null;}
    private static TimelineEventEntry RequireEvent(SqliteConnection c,SqliteTransaction tx,string w,Guid id)=>ReadEvent(c,tx,w,id)??throw new KeyNotFoundException("Timeline event not found.");
    private static PlotThreadEntry RequireThread(SqliteConnection c,SqliteTransaction tx,string w,Guid id)=>ReadThread(c,tx,w,id)??throw new KeyNotFoundException("Plot thread not found.");
    private sealed record RequestReceipt(string Workspace,Guid AggregateId,string Operation,string Fingerprint,string PayloadHash);
    private static RequestReceipt? ReadReceipt(SqliteConnection c,SqliteTransaction tx,Guid id){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT workspace_id,aggregate_id,operation,request_fingerprint,payload_hash FROM timeline_plot_requests WHERE request_id=$id;";cmd.Parameters.AddWithValue("$id",D(id));using var r=cmd.ExecuteReader();return r.Read()?new(r.GetString(0),Guid.Parse(r.GetString(1)),r.GetString(2),r.GetString(3),r.GetString(4)):null;}
    private static void RequireReceipt(RequestReceipt? r,string op,string w,Guid id,string fp,string hash){if(r is null||r.Operation!=op||r.Workspace!=w||r.AggregateId!=id||r.Fingerprint!=fp||r.PayloadHash!=hash)throw new TimelinePlotConflictException("Request identity was reused with different immutable payload.");}
    private static void Receipt(SqliteConnection c,SqliteTransaction tx,Guid request,string w,Guid id,string op,string fp,string hash,long revision,Guid? message,DateTimeOffset at)=>Execute(c,tx,"INSERT INTO timeline_plot_requests(request_id,workspace_id,aggregate_id,operation,request_fingerprint,payload_hash,result_revision,message_id,created_at_utc) VALUES($q,$w,$id,$op,$fp,$hash,$r,$m,$at);",("$q",D(request)),("$w",w),("$id",D(id)),("$op",op),("$fp",fp),("$hash",hash),("$r",revision),("$m",message is null?DBNull.Value:D(message.Value)),("$at",Text(at)));
    private static void Outbox(SqliteConnection c,SqliteTransaction tx,Guid message,string type,object payload,DateTimeOffset at)=>Execute(c,tx,"INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($m,$t,'1.0.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);",("$m",D(message)),("$t",type),("$p",JsonSerializer.Serialize(payload)),("$at",Text(at)));
    private static string Hash<T>(T value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static Guid MessageId(Guid request)=>new(request.ToByteArray().Select((b,i)=>(byte)(b^(i%2==0?0x33:0xCC))).ToArray());
    private static string D(Guid v)=>v.ToString("D");private static string Text(DateTimeOffset v)=>v.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);private static DateTimeOffset Parse(string v)=>DateTimeOffset.Parse(v,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
    private static void Execute(SqliteConnection c,SqliteTransaction tx,string sql,params (string Name,object Value)[] p){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var x in p)cmd.Parameters.AddWithValue(x.Name,x.Value);cmd.ExecuteNonQuery();}
}

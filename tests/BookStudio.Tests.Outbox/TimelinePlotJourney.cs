using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class TimelinePlotJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options=SqliteWorkspaceOptions.Create(workspaceRoot,"timeline-plot.db",64);
        await using var database=new SqliteWorkspaceDatabase(options);
        var health=await database.InitializeAsync();
        Require(health.LatestMigrationVersion>=21,"Timeline plot migration missing.");
        var factory=new SqliteConnectionFactory(options);
        var now=new DateTimeOffset(2026,7,28,23,0,0,TimeSpan.Zero);
        const string workspace="workspace-a";
        var project=Guid.NewGuid();var audit=Guid.NewGuid();var closed=Guid.NewGuid();
        var fact1=Guid.NewGuid();var fact2=Guid.NewGuid();SeedFact(factory,workspace,project,audit,closed,fact1,"departure",now);SeedFact(factory,workspace,project,audit,closed,fact2,"arrival",now.AddHours(1));
        var firstId=Guid.NewGuid();var secondId=Guid.NewGuid();Guid activationMessage;Guid resolvedMessage;
        await using(var store=new SqliteTimelinePlotStore(factory))
        {
            var firstDraft=new TimelineEventDraft(firstId,project,fact1,audit,closed,workspace,"departure",10,now,[],"Mara departs the vault.","auditor","event-1");
            Require(!(await store.CreateEventAsync(firstDraft,now)).Replayed,"First event create failed.");
            Require((await store.CreateEventAsync(firstDraft,now.AddMinutes(1))).Replayed,"Event replay failed.");
            await Throws<TimelinePlotConflictException>(()=>store.CreateEventAsync(firstDraft with{Summary="Changed"},now).AsTask());
            var firstActivate=new TimelineEventControl(Guid.NewGuid(),workspace,firstId,1,"editor","activate-1");
            var activeFirst=await store.ActivateEventAsync(firstActivate,now.AddMinutes(2));Require(activeFirst.Status==TimelineEventStatus.Active,"First event did not activate.");
            var secondDraft=new TimelineEventDraft(secondId,project,fact2,audit,closed,workspace,"arrival",20,now.AddHours(1),[firstId],"Mara reaches the library.","auditor","event-2");
            await store.CreateEventAsync(secondDraft,now.AddMinutes(3));
            var secondActivate=new TimelineEventControl(Guid.NewGuid(),workspace,secondId,1,"editor","activate-2");activationMessage=MessageId(secondActivate.RequestId);
            var activeSecond=await store.ActivateEventAsync(secondActivate,now.AddMinutes(4));Require(activeSecond.Status==TimelineEventStatus.Active,"Second event did not activate.");
            await Throws<TimelinePlotConflictException>(()=>store.CreateEventAsync(secondDraft with{EventId=Guid.NewGuid(),EventKey="self",DependsOnEventIds=[secondId],RequestFingerprint="self"},now).AsTask());
            var threadId=Guid.NewGuid();var threadDraft=new PlotThreadDraft(threadId,project,workspace,"escape-route","Escape route",[firstId,secondId],"planner","thread-create");
            Require(!(await store.CreateThreadAsync(threadDraft,now.AddMinutes(5))).Replayed,"Thread create failed.");
            var start=new PlotThreadAdvance(Guid.NewGuid(),workspace,threadId,1,PlotThreadStatus.Active,firstId,"Departure milestone reached.","editor","thread-start");
            var activeThread=await store.AdvanceThreadAsync(start,now.AddMinutes(6));Require(activeThread.Status==PlotThreadStatus.Active&&activeThread.Milestones.Count==1,"Thread start failed.");
            Require((await store.AdvanceThreadAsync(start,now.AddMinutes(7))).Milestones.Count==1,"Thread replay duplicated milestone.");
            await Throws<TimelinePlotConflictException>(()=>store.AdvanceThreadAsync(start with{Reason="Different"},now.AddMinutes(7)).AsTask());
            var resolve=new PlotThreadAdvance(Guid.NewGuid(),workspace,threadId,2,PlotThreadStatus.Resolved,secondId,"All required events are active.","editor","thread-resolve");resolvedMessage=MessageId(resolve.RequestId);
            var resolved=await store.AdvanceThreadAsync(resolve,now.AddMinutes(8));Require(resolved.Status==PlotThreadStatus.Resolved&&resolved.Milestones.Count==2,"Thread resolve failed.");
            Require(await store.GetEventAsync("workspace-b",firstId) is null&&await store.GetThreadAsync("workspace-b",threadId) is null,"Workspace isolation failed.");
        }
        await using(var restarted=new SqliteTimelinePlotStore(factory))
        {
            Require((await restarted.GetEventAsync(workspace,secondId))?.Status==TimelineEventStatus.Active,"Event restart durability failed.");
            Require((await restarted.GetThreadAsync(workspace,(await FindThread(factory,workspace))!.Value))?.Status==PlotThreadStatus.Resolved,"Thread restart durability failed.");
        }
        await using var outbox=new SqliteOutboxStore(factory);var messages=await outbox.ClaimAsync("timeline-worker",100,TimeSpan.FromMinutes(5),now.AddHours(2));
        Require(messages.Count(x=>x.MessageId==activationMessage&&x.EventType=="editorial.timeline-event.activated")==1,"Timeline activation event was not exactly once.");
        Require(messages.Count(x=>x.MessageId==resolvedMessage&&x.EventType=="editorial.plot-thread.resolved")==1,"Plot resolution event was not exactly once.");
    }

    private static async Task<Guid?> FindThread(SqliteConnectionFactory factory,string workspace){using var c=factory.OpenConnection();using var cmd=c.CreateCommand();cmd.CommandText="SELECT thread_id FROM plot_threads WHERE workspace_id=$w LIMIT 1;";cmd.Parameters.AddWithValue("$w",workspace);var v=await cmd.ExecuteScalarAsync();return v is null?null:Guid.Parse((string)v);}
    private static void SeedFact(SqliteConnectionFactory factory,string workspace,Guid project,Guid audit,Guid closed,Guid fact,string key,DateTimeOffset at)
    {
        using var c=factory.OpenConnection();using var tx=c.BeginTransaction();
        using(var a=c.CreateCommand()){a.Transaction=tx;a.CommandText="INSERT OR IGNORE INTO transition_audits(workspace_id,audit_id,project_id,scope,source_json,target_json,rule_set_version,assessments_json,findings_json,revision,status,closed_message_id,created_at_utc,updated_at_utc) VALUES($w,$a,$p,'SCENE','{}','{}','1.0','[]','[]',10,'CLOSED',$m,$at,$at);";a.Parameters.AddWithValue("$w",workspace);a.Parameters.AddWithValue("$a",audit.ToString("D"));a.Parameters.AddWithValue("$p",project.ToString("D"));a.Parameters.AddWithValue("$m",closed.ToString("D"));a.Parameters.AddWithValue("$at",at.ToString("O"));a.ExecuteNonQuery();}
        using(var k=c.CreateCommand()){k.Transaction=tx;k.CommandText="INSERT INTO knowledge_entries(workspace_id,entry_id,project_id,transition_audit_id,transition_closed_message_id,kind,subject,object_text,statement,evidence,knowners_json,excluded_json,disclosures_json,valid_from_utc,valid_to_utc,actor,revision,status,activation_message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,$m,'FACT',$key,'timeline',$statement,'transition','[]','[]','[]',$at,NULL,'auditor',2,'ACTIVE',$msg,$at,$at);";k.Parameters.AddWithValue("$w",workspace);k.Parameters.AddWithValue("$id",fact.ToString("D"));k.Parameters.AddWithValue("$p",project.ToString("D"));k.Parameters.AddWithValue("$a",audit.ToString("D"));k.Parameters.AddWithValue("$m",closed.ToString("D"));k.Parameters.AddWithValue("$key",key);k.Parameters.AddWithValue("$statement",key+" occurs");k.Parameters.AddWithValue("$msg",Guid.NewGuid().ToString("D"));k.Parameters.AddWithValue("$at",at.ToString("O"));k.ExecuteNonQuery();}tx.Commit();
    }
    private static Guid MessageId(Guid request)=>new(request.ToByteArray().Select((b,i)=>(byte)(b^(i%2==0?0x33:0xCC))).ToArray());
    private static async Task Throws<T>(Func<Task> action) where T:Exception{try{await action();}catch(T){return;}throw new InvalidOperationException($"Expected {typeof(T).Name}.");}
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}

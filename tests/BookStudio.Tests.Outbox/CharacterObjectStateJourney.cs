using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class CharacterObjectStateJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options=SqliteWorkspaceOptions.Create(workspaceRoot,"character-object-state.db",64);
        await using var database=new SqliteWorkspaceDatabase(options);
        var health=await database.InitializeAsync();
        Require(health.LatestMigrationVersion>=20,"Character object state migration missing.");
        var factory=new SqliteConnectionFactory(options);
        var now=new DateTimeOffset(2026,7,28,22,0,0,TimeSpan.Zero);
        const string workspace="workspace-a";
        var project=Guid.NewGuid();var audit=Guid.NewGuid();var closed=Guid.NewGuid();var knowledge=Guid.NewGuid();
        SeedAuthority(factory,workspace,project,audit,closed,knowledge,now);
        var stateId=Guid.NewGuid();
        var draft=new NarrativeStateDraft(stateId,project,knowledge,audit,closed,workspace,NarrativeEntityKind.Object,"key-01","holder","Mara","vault","Mara","key",true,now,null,"auditor","create-key");
        Guid activationMessage;Guid transferMessage;
        await using(var store=new SqliteCharacterObjectStateStore(factory))
        {
            var created=await store.CreateAsync(draft,now.AddMinutes(1));Require(!created.Replayed&&created.Entry.Status==NarrativeStateStatus.Draft,"Create failed.");
            Require((await store.CreateAsync(draft,now.AddMinutes(2))).Replayed,"Create replay failed.");
            await Throws<NarrativeStateConflictException>(()=>store.CreateAsync(draft with{Actor="other"},now.AddMinutes(2)).AsTask());
            var activate=new NarrativeStateControlCommand(Guid.NewGuid(),workspace,stateId,1,"editor","activate");
            var active=await store.ActivateAsync(activate,now.AddMinutes(3));activationMessage=active.ActivationMessageId??throw new InvalidOperationException("Activation message missing.");Require(active.Status==NarrativeStateStatus.Active&&active.Revision==2,"Activation failed.");
            Require((await store.ActivateAsync(activate,now.AddMinutes(4))).Revision==2,"Activation replay changed revision.");
            await Throws<NarrativeStateConflictException>(()=>store.TransferAsync(new(Guid.NewGuid(),workspace,stateId,2,"Ivo","Nora","library","editor","bad-origin"),now.AddMinutes(5)).AsTask());
            var transfer=new ObjectTransferCommand(Guid.NewGuid(),workspace,stateId,2,"Mara","Nora","library","editor","transfer");transferMessage=MessageId(transfer.RequestId);
            var moved=await store.TransferAsync(transfer,now.AddMinutes(6));Require(moved.Holder=="Nora"&&moved.Location=="library"&&moved.Transfers.Count==1&&moved.Revision==3,"Transfer failed.");
            Require((await store.TransferAsync(transfer,now.AddMinutes(7))).Transfers.Count==1,"Transfer replay duplicated inventory movement.");
            await Throws<NarrativeStateConflictException>(()=>store.TransferAsync(new(Guid.NewGuid(),workspace,stateId,2,"Nora","Ivo",null,"editor","stale"),now.AddMinutes(8)).AsTask());
            Require(await store.GetAsync("workspace-b",stateId) is null,"Workspace isolation failed.");
        }
        await using(var restarted=new SqliteCharacterObjectStateStore(factory))
        {
            var durable=await restarted.GetAsync(workspace,stateId)??throw new InvalidOperationException("State missing after restart.");Require(durable.Holder=="Nora"&&durable.Transfers.Count==1,"Restart durability failed.");
        }
        await using var outbox=new SqliteOutboxStore(factory);var messages=await outbox.ClaimAsync("state-worker",100,TimeSpan.FromMinutes(5),now.AddHours(1));
        Require(messages.Count(x=>x.MessageId==activationMessage&&x.EventType=="editorial.character-object-state.activated")==1,"Activation event was not exactly once.");
        Require(messages.Count(x=>x.MessageId==transferMessage&&x.EventType=="editorial.character-object-state.transferred")==1,"Transfer event was not exactly once.");
    }

    private static void SeedAuthority(SqliteConnectionFactory factory,string workspace,Guid project,Guid audit,Guid closed,Guid knowledge,DateTimeOffset at)
    {
        using var c=factory.OpenConnection();using var tx=c.BeginTransaction();
        using(var a=c.CreateCommand()){a.Transaction=tx;a.CommandText="INSERT INTO transition_audits(workspace_id,audit_id,project_id,scope,source_json,target_json,rule_set_version,assessments_json,findings_json,revision,status,closed_message_id,created_at_utc,updated_at_utc) VALUES($w,$a,$p,'SCENE','{}','{}','1.0','[]','[]',10,'CLOSED',$m,$at,$at);";a.Parameters.AddWithValue("$w",workspace);a.Parameters.AddWithValue("$a",audit.ToString("D"));a.Parameters.AddWithValue("$p",project.ToString("D"));a.Parameters.AddWithValue("$m",closed.ToString("D"));a.Parameters.AddWithValue("$at",at.ToString("O"));a.ExecuteNonQuery();}
        using(var k=c.CreateCommand()){k.Transaction=tx;k.CommandText="INSERT INTO knowledge_entries(workspace_id,entry_id,project_id,transition_audit_id,transition_closed_message_id,kind,subject,object_text,statement,evidence,knowners_json,excluded_json,disclosures_json,valid_from_utc,valid_to_utc,actor,revision,status,activation_message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,$m,'FACT','key-01','holder','Mara holds the key','transition','[]','[]','[]',$at,NULL,'auditor',2,'ACTIVE',$msg,$at,$at);";k.Parameters.AddWithValue("$w",workspace);k.Parameters.AddWithValue("$id",knowledge.ToString("D"));k.Parameters.AddWithValue("$p",project.ToString("D"));k.Parameters.AddWithValue("$a",audit.ToString("D"));k.Parameters.AddWithValue("$m",closed.ToString("D"));k.Parameters.AddWithValue("$msg",Guid.NewGuid().ToString("D"));k.Parameters.AddWithValue("$at",at.ToString("O"));k.ExecuteNonQuery();}
        tx.Commit();
    }
    private static Guid MessageId(Guid request)=>new(request.ToByteArray().Select((b,i)=>(byte)(b^(i%2==0?0x55:0xAA))).ToArray());
    private static async Task Throws<T>(Func<Task> action) where T:Exception{try{await action();}catch(T){return;}throw new InvalidOperationException($"Expected {typeof(T).Name}.");}
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}

using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class KnowledgeStateJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options=SqliteWorkspaceOptions.Create(workspaceRoot,"knowledge-state.db",64);
        await using var database=new SqliteWorkspaceDatabase(options);
        var health=await database.InitializeAsync();
        Require(health.LatestMigrationVersion>=19,"Knowledge state migration missing.");
        var factory=new SqliteConnectionFactory(options);
        var now=new DateTimeOffset(2026,7,28,20,0,0,TimeSpan.Zero);
        const string workspace="workspace-a";
        var project=Guid.NewGuid();var audit=Guid.NewGuid();var closed=Guid.NewGuid();SeedAuthority(factory,workspace,project,audit,closed,now);
        var entryId=Guid.NewGuid();
        var draft=new KnowledgeDraft(entryId,project,audit,closed,workspace,KnowledgeKind.Secret,"Mara","cabinet-route","The cabinet reveals a hidden route.","Closed transition proves discovery.",new[]{"Mara"},new[]{"Ivo"},now,null,"auditor","create");
        Guid activationMessage;
        await using(var store=new SqliteKnowledgeStateStore(factory))
        {
            var created=await store.CreateAsync(draft,now.AddMinutes(1));Require(!created.Replayed&&created.Entry.Status==KnowledgeStatus.Draft,"Create failed.");
            Require((await store.CreateAsync(draft,now.AddMinutes(2))).Replayed,"Create replay failed.");
            await Throws<KnowledgeConflictException>(()=>store.CreateAsync(draft with { Statement="Different" },now.AddMinutes(2)).AsTask());
            await Throws<KnowledgeValidationException>(()=>store.CreateAsync(draft with { EntryId=Guid.NewGuid(),TransitionClosedMessageId=Guid.NewGuid() },now.AddMinutes(2)).AsTask());
            var activate=new KnowledgeControlCommand(Guid.NewGuid(),workspace,entryId,1,"editor","activate");
            var active=await store.ActivateAsync(activate,now.AddMinutes(3));activationMessage=active.ActivationMessageId??throw new InvalidOperationException("Activation message missing.");Require(active.Status==KnowledgeStatus.Active&&active.Revision==2,"Activation failed.");
            Require((await store.ActivateAsync(activate,now.AddMinutes(4))).Revision==2,"Activation replay changed revision.");
            await Throws<KnowledgeConflictException>(()=>store.ActivateAsync(activate with { RequestFingerprint="different" },now.AddMinutes(4)).AsTask());
            await Throws<KnowledgeConflictException>(()=>store.CreateAsync(draft with { EntryId=Guid.NewGuid(),Statement="The cabinet contains no route.",RequestFingerprint="contradiction" },now.AddMinutes(4)).AsTask());
            await Throws<KnowledgeValidationException>(()=>store.DiscloseAsync(new(Guid.NewGuid(),workspace,entryId,2,new[]{"Ivo"},"bad","editor","bad-disclosure"),now.AddMinutes(5)).AsTask());
            var disclosure=new KnowledgeDisclosureCommand(Guid.NewGuid(),workspace,entryId,2,new[]{"Nora"},"Mara tells Nora after trust is established.","editor","disclose");
            var disclosed=await store.DiscloseAsync(disclosure,now.AddMinutes(6));Require(disclosed.Knowners.SequenceEqual(new[]{"Mara","Nora"})&&disclosed.Disclosures.Count==1,"Disclosure failed.");
            await Throws<KnowledgeConflictException>(()=>store.RetractAsync(new(Guid.NewGuid(),workspace,entryId,2,"stale","editor","stale"),now.AddMinutes(7)).AsTask());
            var retract=new KnowledgeTerminalCommand(Guid.NewGuid(),workspace,entryId,3,"Source was proven fabricated.","editor","retract");
            var retracted=await store.RetractAsync(retract,now.AddMinutes(8));Require(retracted.Status==KnowledgeStatus.Retracted&&retracted.Revision==4,"Retraction failed.");
            Require(await store.GetAsync("workspace-b",entryId) is null,"Workspace isolation failed.");
        }
        await using(var restarted=new SqliteKnowledgeStateStore(factory))
        {
            var durable=await restarted.GetAsync(workspace,entryId)??throw new InvalidOperationException("Entry missing after restart.");Require(durable.Status==KnowledgeStatus.Retracted&&durable.Disclosures.Count==1,"Restart durability failed.");
        }
        await using var outbox=new SqliteOutboxStore(factory);var messages=await outbox.ClaimAsync("knowledge-worker",100,TimeSpan.FromMinutes(5),now.AddHours(1));Require(messages.Count(x=>x.MessageId==activationMessage&&x.EventType=="editorial.knowledge-state.activated")==1,"Activation event was not exactly once.");
    }

    private static void SeedAuthority(SqliteConnectionFactory factory,string workspace,Guid project,Guid audit,Guid closed,DateTimeOffset at)
    {
        using var c=factory.OpenConnection();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO transition_audits(workspace_id,audit_id,project_id,scope,source_json,target_json,rule_set_version,assessments_json,findings_json,revision,status,closed_message_id,created_at_utc,updated_at_utc) VALUES($w,$a,$p,'SCENE','{}','{}','1.0','[]','[]',10,'CLOSED',$m,$at,$at);";cmd.Parameters.AddWithValue("$w",workspace);cmd.Parameters.AddWithValue("$a",audit.ToString("D"));cmd.Parameters.AddWithValue("$p",project.ToString("D"));cmd.Parameters.AddWithValue("$m",closed.ToString("D"));cmd.Parameters.AddWithValue("$at",at.ToString("O"));cmd.ExecuteNonQuery();tx.Commit();
    }
    private static async Task Throws<T>(Func<Task> action) where T:Exception{try{await action();}catch(T){return;}throw new InvalidOperationException($"Expected {typeof(T).Name}.");}
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}

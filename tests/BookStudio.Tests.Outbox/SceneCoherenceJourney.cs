using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class SceneCoherenceJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options=SqliteWorkspaceOptions.Create(workspaceRoot,"scene-coherence.db",64);
        await using var database=new SqliteWorkspaceDatabase(options);
        var health=await database.InitializeAsync();
        Require(health.LatestMigrationVersion>=17,"Scene coherence migration missing.");
        var factory=new SqliteConnectionFactory(options);
        var now=new DateTimeOffset(2026,7,28,19,0,0,TimeSpan.Zero);
        const string workspace="workspace-a";
        var project=Guid.NewGuid();var plan=Guid.NewGuid();var generation=Guid.NewGuid();var approval=Guid.NewGuid();
        const string text="Mara finds the brass key. She uses it to open the sealed cabinet. The discovery forces her to abandon the original route.";
        var digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        SeedAuthority(factory,workspace,project,plan,generation,approval,digest,text,now);
        var auditId=Guid.NewGuid();
        var draft=new SceneCoherenceDraft(auditId,project,generation,approval,digest,plan,1,"scene-1",workspace,"scene-rules-1",text,"auditor","create");
        Guid messageId;

        await using(var store=new SqliteSceneCoherenceStore(factory))
        {
            var created=await store.CreateAsync(draft,now.AddMinutes(1));
            Require(!created.Replayed&&created.Audit.PlannedBeats.Count==2&&created.Audit.EntryState=="ROOT","Authority load failed.");
            Require((await store.CreateAsync(draft,now.AddMinutes(2))).Replayed,"Create replay failed.");
            await Throws<SceneCoherenceConflictException>(()=>store.CreateAsync(draft with { RuleSetVersion="other" },now.AddMinutes(2)).AsTask());
            await Throws<SceneCoherenceValidationException>(()=>store.CreateAsync(draft with { AuditId=Guid.NewGuid(),SceneApprovalMessageId=Guid.NewGuid() },now.AddMinutes(2)).AsTask());

            var start=new SceneCoherenceControlCommand(Guid.NewGuid(),workspace,auditId,1,"auditor","start");
            var running=await store.StartAsync(start,now.AddMinutes(3));
            Require(running.Status==SceneCoherenceStatus.Running&&running.Revision==2,"Start failed.");
            Require((await store.StartAsync(start,now.AddMinutes(4))).Revision==2,"Start replay changed revision.");

            var beat1=new SceneBeatAssessmentCommand(Guid.NewGuid(),workspace,auditId,2,"Find the brass key",1,SceneBeatStatus.Satisfied,0,24,"The opening sentence establishes the discovery.","auditor","beat-1");
            var afterBeat1=await store.AssessBeatAsync(beat1,now.AddMinutes(5));
            await Throws<SceneCoherenceValidationException>(()=>store.AssessBeatAsync(new(Guid.NewGuid(),workspace,auditId,3,"Open the sealed cabinet",1,SceneBeatStatus.Satisfied,25,39,"wrong order","auditor","wrong-order"),now.AddMinutes(5)).AsTask());
            var beat2=new SceneBeatAssessmentCommand(Guid.NewGuid(),workspace,auditId,3,"Open the sealed cabinet",2,SceneBeatStatus.Satisfied,25,39,"The second sentence completes the planned action.","auditor","beat-2");
            var afterBeat2=await store.AssessBeatAsync(beat2,now.AddMinutes(6));
            Require(afterBeat2.BeatAssessments.Count==2,"Beat coverage failed.");

            var link=new SceneCausalLinkCommand(Guid.NewGuid(),workspace,auditId,4,Guid.NewGuid(),0,24,25,39,SceneCausalStatus.Supported,"Finding the key enables opening the cabinet.","auditor","link");
            var linked=await store.RecordCausalLinkAsync(link,now.AddMinutes(7));
            Require(linked.CausalLinks.Count==1,"Causal link failed.");
            await Throws<SceneCoherenceValidationException>(()=>store.RecordCausalLinkAsync(link with { RequestId=Guid.NewGuid(),ExpectedRevision=5,LinkId=Guid.NewGuid(),CauseStartOffset=80,EffectStartOffset=20,RequestFingerprint="bad-link" },now.AddMinutes(7)).AsTask());

            var findingId=Guid.NewGuid();
            var finding=new SceneCoherenceFindingCommand(Guid.NewGuid(),workspace,auditId,5,findingId,"EXIT-001","1.0",SceneCoherenceFindingCategory.ExitState,SceneCoherenceSeverity.Blocking,65,text.Length-65,"Exit-state consequence needs explicit confirmation.","Confirm the route change as the scene exit state.","auditor","finding");
            var found=await store.RecordFindingAsync(finding,now.AddMinutes(8));
            Require(found.Findings.Count==1,"Finding append failed.");
            await Throws<SceneCoherenceConflictException>(()=>store.ReviewAsync(new(Guid.NewGuid(),workspace,auditId,5,"auditor","stale"),now.AddMinutes(9)).AsTask());
            var reviewed=await store.ReviewAsync(new(Guid.NewGuid(),workspace,auditId,6,"auditor","review"),now.AddMinutes(9));
            Require(reviewed.Status==SceneCoherenceStatus.Reviewed,"Review failed.");
            await Throws<SceneCoherenceTransitionException>(()=>store.CloseAsync(new(Guid.NewGuid(),workspace,auditId,7,"publisher","blocked","close-blocked"),now.AddMinutes(10)).AsTask());

            var decided=await store.DecideFindingAsync(new(Guid.NewGuid(),workspace,auditId,7,findingId,SceneCoherenceDecision.Resolved,"The final sentence explicitly establishes the changed route.","editor","resolve"),now.AddMinutes(11));
            Require(decided.Findings[0].Decision==SceneCoherenceDecision.Resolved,"Decision failed.");
            var close=new SceneCoherenceCloseCommand(Guid.NewGuid(),workspace,auditId,8,"publisher","Scene coherence accepted.","close");
            var closed=await store.CloseAsync(close,now.AddMinutes(12));messageId=closed.MessageId;
            Require(!closed.Replayed&&closed.Audit.Status==SceneCoherenceStatus.Closed,"Close failed.");
            Require((await store.CloseAsync(close,now.AddMinutes(13))).Replayed,"Close replay failed.");
            await Throws<SceneCoherenceConflictException>(()=>store.CloseAsync(close with { RequestFingerprint="different" },now.AddMinutes(13)).AsTask());
            Require(await store.GetAsync("workspace-b",auditId) is null,"Workspace isolation failed.");
        }

        await using(var restarted=new SqliteSceneCoherenceStore(factory))
        {
            var durable=await restarted.GetAsync(workspace,auditId)??throw new InvalidOperationException("Audit missing after restart.");
            Require(durable.Status==SceneCoherenceStatus.Closed&&durable.BeatAssessments.Count==2&&durable.CausalLinks.Count==1&&durable.Findings.Count==1,"Restart durability failed.");
        }

        await using var outbox=new SqliteOutboxStore(factory);
        var messages=await outbox.ClaimAsync("scene-coherence-worker",100,TimeSpan.FromMinutes(5),now.AddHours(1));
        Require(messages.Count(x=>x.MessageId==messageId&&x.EventType=="editorial.scene-coherence.closed")==1,"Close event was not exactly once.");
    }

    private static void SeedAuthority(SqliteConnectionFactory factory,string workspace,Guid project,Guid plan,Guid generation,Guid approval,string digest,string text,DateTimeOffset at)
    {
        var planContent=new ScenePlanContent(new[]{new PlannedScene("scene-1","chapter-1",1,"The sealed cabinet","Reveal the hidden route","Mara changes course after the discovery.",new[]{"Find the brass key","Open the sealed cabinet"},Array.Empty<string>(),Array.Empty<string>(),new[]{"Mara abandons the original route"},Array.Empty<string>())},Array.Empty<string>(),Array.Empty<string>());
        using var c=factory.OpenConnection();using var tx=c.BeginTransaction();
        Exec(c,tx,"INSERT INTO scene_plans(workspace_id,scene_plan_id,project_id,book_plan_id,book_plan_version,book_plan_approval_message_id,book_plan_content_digest,schema_version,current_version,approval_message_id,created_at_utc,updated_at_utc) VALUES($w,$sp,$p,$bp,1,$bm,$bd,'1.0.0',1,$am,$at,$at);",("$w",workspace),("$sp",plan.ToString("D")),("$p",project.ToString("D")),("$bp",Guid.NewGuid().ToString("D")),("$bm",Guid.NewGuid().ToString("D")),("$bd",new string('b',64)),("$am",Guid.NewGuid().ToString("D")),("$at",at.ToString("O")));
        Exec(c,tx,"INSERT INTO scene_plan_versions(workspace_id,scene_plan_id,version,revision,status,content_json,content_digest,actor,reason,created_at_utc,updated_at_utc) VALUES($w,$sp,1,4,'APPROVED',$j,$d,'planner','approved',$at,$at);",("$w",workspace),("$sp",plan.ToString("D")),("$j",JsonSerializer.Serialize(planContent)),("$d",new string('c',64)),("$at",at.ToString("O")));
        Exec(c,tx,"INSERT INTO scene_generations(workspace_id,generation_id,project_id,scene_plan_id,scene_plan_version,scene_plan_approval_message_id,scene_plan_content_digest,schema_version,brief_json,revision,status,approval_message_id,created_at_utc,updated_at_utc) VALUES($w,$g,$p,$sp,1,$pm,$pd,'1.0.0','{}',7,'APPROVED',$am,$at,$at);",("$w",workspace),("$g",generation.ToString("D")),("$p",project.ToString("D")),("$sp",plan.ToString("D")),("$pm",Guid.NewGuid().ToString("D")),("$pd",new string('c',64)),("$am",approval.ToString("D")),("$at",at.ToString("O")));
        Exec(c,tx,"INSERT INTO scene_generation_attempts(workspace_id,generation_id,attempt,status,invocation_json,generated_text,content_digest,acceptance_evidence_json,error_class,error_text,retryable,actor,started_at_utc,finished_at_utc) VALUES($w,$g,1,'GENERATED','{}',$t,$d,'[]',NULL,NULL,NULL,'worker',$at,$at);",("$w",workspace),("$g",generation.ToString("D")),("$t",text),("$d",digest),("$at",at.ToString("O")));tx.Commit();
    }

    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection c,Microsoft.Data.Sqlite.SqliteTransaction tx,string sql,params (string,object)[] values){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var v in values)cmd.Parameters.AddWithValue(v.Item1,v.Item2);cmd.ExecuteNonQuery();}
    private static async Task Throws<T>(Func<Task> action) where T:Exception{try{await action();}catch(T){return;}throw new InvalidOperationException($"Expected {typeof(T).Name}.");}
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}

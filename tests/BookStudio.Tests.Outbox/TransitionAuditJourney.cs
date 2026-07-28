using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class TransitionAuditJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options=SqliteWorkspaceOptions.Create(workspaceRoot,"transition-audit.db",64);
        await using var database=new SqliteWorkspaceDatabase(options);
        var health=await database.InitializeAsync();
        Require(health.LatestMigrationVersion>=18,"Transition audit migration missing.");
        var factory=new SqliteConnectionFactory(options);
        var now=new DateTimeOffset(2026,7,28,20,0,0,TimeSpan.Zero);
        const string workspace="workspace-a";
        var project=Guid.NewGuid(); var sourceId=Guid.NewGuid(); var targetId=Guid.NewGuid();
        const string sourceDigest="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string targetDigest="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        SeedClosedScene(factory,workspace,project,sourceId,sourceDigest,"{\"time\":\"morning\",\"location\":\"archive\"}",now);
        SeedClosedScene(factory,workspace,project,targetId,targetDigest,"{\"time\":\"noon\",\"location\":\"courtyard\"}",now);
        var auditId=Guid.NewGuid();
        var draft=new TransitionAuditDraft(auditId,project,workspace,TransitionScope.Scene,new("SCENE",sourceId,5,sourceDigest,"{\"time\":\"morning\"}"),new("SCENE",targetId,5,targetDigest,"{\"time\":\"noon\"}"),"transition-rules-1","auditor","create");
        Guid messageId;

        await using(var store=new SqliteTransitionAuditStore(factory))
        {
            var created=await store.CreateAsync(draft,now.AddMinutes(1));
            Require(!created.Replayed&&created.Audit.Status==TransitionAuditStatus.Draft,"Create failed.");
            Require((await store.CreateAsync(draft,now.AddMinutes(2))).Replayed,"Create replay failed.");
            await Throws<TransitionAuditConflictException>(()=>store.CreateAsync(draft with { RuleSetVersion="other" },now.AddMinutes(2)).AsTask());
            await Throws<TransitionAuditValidationException>(()=>store.CreateAsync(draft with { AuditId=Guid.NewGuid(),Source=draft.Source with { ContentDigest=targetDigest } },now.AddMinutes(2)).AsTask());

            var start=new TransitionAuditControlCommand(Guid.NewGuid(),workspace,auditId,1,"auditor","start");
            var running=await store.StartAsync(start,now.AddMinutes(3));
            Require(running.Status==TransitionAuditStatus.Running&&running.Revision==2,"Start failed.");
            Require((await store.StartAsync(start,now.AddMinutes(4))).Revision==2,"Start replay changed revision.");

            var revision=2L;
            foreach(var dimension in Enum.GetValues<TransitionDimension>())
            {
                var assessed=await store.AssessDimensionAsync(new(Guid.NewGuid(),workspace,auditId,revision,dimension,dimension==TransitionDimension.Tone?TransitionAssessmentStatus.NotApplicable:TransitionAssessmentStatus.Supported,$"Evidence for {dimension}.","auditor",$"assess-{dimension}"),now.AddMinutes(4+revision));
                revision=assessed.Revision;
            }
            Require(revision==10,"Dimension assessment revisions are incorrect.");
            await Throws<TransitionAuditConflictException>(()=>store.AssessDimensionAsync(new(Guid.NewGuid(),workspace,auditId,revision,TransitionDimension.Time,TransitionAssessmentStatus.Supported,"duplicate","auditor","duplicate"),now.AddMinutes(15)).AsTask());

            var findingId=Guid.NewGuid();
            var found=await store.RecordFindingAsync(new(Guid.NewGuid(),workspace,auditId,revision,findingId,"TR-OBJ-001","1.0",TransitionSeverity.Blocking,"The objective handoff needs explicit acknowledgement.","Add one sentence linking the objective.","auditor","finding"),now.AddMinutes(16));
            revision=found.Revision;
            await Throws<TransitionAuditConflictException>(()=>store.ReviewAsync(new(Guid.NewGuid(),workspace,auditId,revision-1,"auditor","stale"),now.AddMinutes(17)).AsTask());
            var reviewed=await store.ReviewAsync(new(Guid.NewGuid(),workspace,auditId,revision,"auditor","review"),now.AddMinutes(18));
            revision=reviewed.Revision;
            Require(reviewed.Status==TransitionAuditStatus.Reviewed,"Review failed.");
            await Throws<TransitionAuditTransitionException>(()=>store.CloseAsync(new(Guid.NewGuid(),workspace,auditId,revision,"publisher","blocked","close-blocked"),now.AddMinutes(19)).AsTask());
            var decided=await store.DecideFindingAsync(new(Guid.NewGuid(),workspace,auditId,revision,findingId,TransitionDecision.Resolved,"The target opening now carries the source objective.","editor","resolve"),now.AddMinutes(20));
            revision=decided.Revision;
            var close=new TransitionAuditCloseCommand(Guid.NewGuid(),workspace,auditId,revision,"publisher","Transition accepted.","close");
            var closed=await store.CloseAsync(close,now.AddMinutes(21)); messageId=closed.MessageId;
            Require(!closed.Replayed&&closed.Audit.Status==TransitionAuditStatus.Closed,"Close failed.");
            Require((await store.CloseAsync(close,now.AddMinutes(22))).Replayed,"Close replay failed.");
            await Throws<TransitionAuditConflictException>(()=>store.CloseAsync(close with { RequestFingerprint="different" },now.AddMinutes(22)).AsTask());
            Require(await store.GetAsync("workspace-b",auditId) is null,"Workspace isolation failed.");
        }

        await using(var restarted=new SqliteTransitionAuditStore(factory))
        {
            var durable=await restarted.GetAsync(workspace,auditId)??throw new InvalidOperationException("Audit missing after restart.");
            Require(durable.Status==TransitionAuditStatus.Closed&&durable.Assessments.Count==8&&durable.Findings.Count==1,"Restart durability failed.");
        }

        await using var outbox=new SqliteOutboxStore(factory);
        var messages=await outbox.ClaimAsync("transition-worker",100,TimeSpan.FromMinutes(5),now.AddHours(1));
        Require(messages.Count(x=>x.MessageId==messageId&&x.EventType=="editorial.transition-audit.closed")==1,"Close event was not exactly once.");
    }

    private static void SeedClosedScene(SqliteConnectionFactory factory,string workspace,Guid project,Guid auditId,string digest,string stateJson,DateTimeOffset at)
    {
        using var c=factory.OpenConnection(); using var tx=c.BeginTransaction();
        Exec(c,tx,"INSERT INTO scene_coherence_audits(workspace_id,audit_id,project_id,generation_id,scene_approval_message_id,scene_content_digest,scene_plan_id,scene_plan_version,scene_key,rule_set_version,source_text,entry_state,exit_state,planned_beats_json,beat_assessments_json,causal_links_json,findings_json,revision,status,closed_message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$g,$m,$d,$sp,1,$k,'rules','text',$e,$x,'[]','[]','[]','[]',5,'CLOSED',$cm,$at,$at);",("$w",workspace),("$id",auditId.ToString("D")),("$p",project.ToString("D")),("$g",Guid.NewGuid().ToString("D")),("$m",Guid.NewGuid().ToString("D")),("$d",digest),("$sp",Guid.NewGuid().ToString("D")),("$k",auditId.ToString("N")),("$e",stateJson),("$x",stateJson),("$cm",Guid.NewGuid().ToString("D")),("$at",at.ToString("O"))); tx.Commit();
    }

    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection c,Microsoft.Data.Sqlite.SqliteTransaction tx,string sql,params (string,object)[] values){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var v in values)cmd.Parameters.AddWithValue(v.Item1,v.Item2);cmd.ExecuteNonQuery();}
    private static async Task Throws<T>(Func<Task> action) where T:Exception{try{await action();}catch(T){return;}throw new InvalidOperationException($"Expected {typeof(T).Name}.");}
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}

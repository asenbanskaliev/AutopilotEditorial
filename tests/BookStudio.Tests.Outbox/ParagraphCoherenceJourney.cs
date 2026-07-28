using System.Security.Cryptography;
using System.Text;
using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class ParagraphCoherenceJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options=SqliteWorkspaceOptions.Create(workspaceRoot,"paragraph-coherence.db",64);
        await using var database=new SqliteWorkspaceDatabase(options);
        var health=await database.InitializeAsync();
        Require(health.LatestMigrationVersion>=16,"Paragraph coherence migration missing.");
        var factory=new SqliteConnectionFactory(options);
        var now=new DateTimeOffset(2026,7,28,18,0,0,TimeSpan.Zero);
        const string workspace="workspace-a";
        var project=Guid.NewGuid();var generation=Guid.NewGuid();var approval=Guid.NewGuid();
        const string text="Mara entered the archive. She carried the brass key.\n\nThe key opened the sealed cabinet. The discovery changed her plan.";
        var digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        SeedApprovedScene(factory,workspace,project,generation,approval,digest,text,now);
        var auditId=Guid.NewGuid();
        var draft=new ParagraphCoherenceDraft(auditId,project,generation,approval,digest,workspace,"local-rules-1",text,"auditor","create-audit");
        Guid closedMessage;

        await using(var store=new SqliteParagraphCoherenceStore(factory))
        {
            var created=await store.CreateAsync(draft,now.AddMinutes(1));
            Require(!created.Replayed&&created.Audit.Paragraphs.Count==2&&created.Audit.Paragraphs[1].StartOffset>created.Audit.Paragraphs[0].StartOffset,"Stable paragraph segmentation failed.");
            Require((await store.CreateAsync(draft,now.AddMinutes(2))).Replayed,"Create replay failed.");
            await Throws<ParagraphCoherenceConflictException>(()=>store.CreateAsync(draft with { RuleSetVersion="other" },now.AddMinutes(2)).AsTask());
            await Throws<ParagraphCoherenceValidationException>(()=>store.CreateAsync(draft with { AuditId=Guid.NewGuid(),SceneApprovalMessageId=Guid.NewGuid() },now.AddMinutes(2)).AsTask());

            var start=new ParagraphCoherenceCommand(Guid.NewGuid(),workspace,auditId,1,"auditor","start");
            var running=await store.StartAsync(start,now.AddMinutes(3));
            Require(running.Status==ParagraphCoherenceStatus.Running&&running.Revision==2,"Audit start failed.");
            Require((await store.StartAsync(start,now.AddMinutes(4))).Revision==2,"Start replay changed revision.");

            var findingId=Guid.NewGuid();var p=running.Paragraphs[0];
            var finding=new ParagraphFindingCommand(Guid.NewGuid(),workspace,auditId,2,findingId,"REF-001","1.0",ParagraphFindingCategory.Reference,ParagraphFindingSeverity.Blocking,1,p.StartOffset,p.Length,"Pronoun antecedent needs confirmation.","Name the antecedent explicitly.","auditor","finding-1");
            var found=await store.RecordFindingAsync(finding,now.AddMinutes(5));
            Require(found.Findings.Count==1&&found.Findings[0].Decision==ParagraphFindingDecision.Open,"Finding append failed.");
            await Throws<ParagraphCoherenceValidationException>(()=>store.RecordFindingAsync(finding with { RequestId=Guid.NewGuid(),ExpectedRevision=3,StartOffset=p.StartOffset+p.Length },now.AddMinutes(5)).AsTask());
            await Throws<ParagraphCoherenceConflictException>(()=>store.ReviewAsync(new(Guid.NewGuid(),workspace,auditId,2,"auditor","stale"),now.AddMinutes(6)).AsTask());

            var reviewed=await store.ReviewAsync(new(Guid.NewGuid(),workspace,auditId,3,"auditor","review"),now.AddMinutes(6));
            Require(reviewed.Status==ParagraphCoherenceStatus.Reviewed,"Review failed.");
            await Throws<ParagraphCoherenceTransitionException>(()=>store.CloseAsync(new(Guid.NewGuid(),workspace,auditId,4,"publisher","close blocked","close-blocked"),now.AddMinutes(7)).AsTask());

            var decided=await store.DecideFindingAsync(new(Guid.NewGuid(),workspace,auditId,4,findingId,ParagraphFindingDecision.Resolved,"Text revised in accepted editorial decision.","editor","resolve"),now.AddMinutes(8));
            Require(decided.Findings[0].Decision==ParagraphFindingDecision.Resolved&&decided.Status==ParagraphCoherenceStatus.Reviewed,"Finding decision failed.");

            var close=new ParagraphCoherenceCloseCommand(Guid.NewGuid(),workspace,auditId,5,"publisher","Local coherence accepted.","close");
            var closed=await store.CloseAsync(close,now.AddMinutes(9));closedMessage=closed.MessageId;
            Require(!closed.Replayed&&closed.Audit.Status==ParagraphCoherenceStatus.Closed,"Close failed.");
            Require((await store.CloseAsync(close,now.AddMinutes(10))).Replayed,"Close replay failed.");
            await Throws<ParagraphCoherenceConflictException>(()=>store.CloseAsync(close with { RequestFingerprint="different" },now.AddMinutes(10)).AsTask());
            Require(await store.GetAsync("workspace-b",auditId) is null,"Workspace isolation failed.");
        }

        await using(var restarted=new SqliteParagraphCoherenceStore(factory))
        {
            var durable=await restarted.GetAsync(workspace,auditId)??throw new InvalidOperationException("Audit missing after restart.");
            Require(durable.Status==ParagraphCoherenceStatus.Closed&&durable.Findings.Count==1&&durable.Paragraphs.Count==2,"Restart durability failed.");
        }

        await using var outbox=new SqliteOutboxStore(factory);
        var messages=await outbox.ClaimAsync("paragraph-worker",100,TimeSpan.FromMinutes(5),now.AddHours(1));
        Require(messages.Count(x=>x.MessageId==closedMessage&&x.EventType=="editorial.paragraph-coherence.closed")==1,"Close event was not exactly once.");
    }

    private static void SeedApprovedScene(SqliteConnectionFactory factory,string workspace,Guid project,Guid generation,Guid approval,string digest,string text,DateTimeOffset at)
    {
        using var c=factory.OpenConnection();using var tx=c.BeginTransaction();
        using var g=c.CreateCommand();g.Transaction=tx;g.CommandText="INSERT INTO scene_generations(workspace_id,generation_id,project_id,scene_plan_id,scene_plan_version,scene_plan_approval_message_id,scene_plan_content_digest,schema_version,brief_json,revision,status,approval_message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$sp,1,$pm,$pd,'1.0.0','{}',7,'APPROVED',$m,$at,$at);";g.Parameters.AddWithValue("$w",workspace);g.Parameters.AddWithValue("$id",generation.ToString("D"));g.Parameters.AddWithValue("$p",project.ToString("D"));g.Parameters.AddWithValue("$sp",Guid.NewGuid().ToString("D"));g.Parameters.AddWithValue("$pm",Guid.NewGuid().ToString("D"));g.Parameters.AddWithValue("$pd",new string('a',64));g.Parameters.AddWithValue("$m",approval.ToString("D"));g.Parameters.AddWithValue("$at",at.ToString("O"));g.ExecuteNonQuery();
        using var a=c.CreateCommand();a.Transaction=tx;a.CommandText="INSERT INTO scene_generation_attempts(workspace_id,generation_id,attempt,status,invocation_json,generated_text,content_digest,acceptance_evidence_json,error_class,error_text,retryable,actor,started_at_utc,finished_at_utc) VALUES($w,$id,1,'GENERATED','{}',$t,$d,'[]',NULL,NULL,NULL,'worker',$at,$at);";a.Parameters.AddWithValue("$w",workspace);a.Parameters.AddWithValue("$id",generation.ToString("D"));a.Parameters.AddWithValue("$t",text);a.Parameters.AddWithValue("$d",digest);a.Parameters.AddWithValue("$at",at.ToString("O"));a.ExecuteNonQuery();tx.Commit();
    }

    private static async Task Throws<T>(Func<Task> action) where T:Exception{try{await action();}catch(T){return;}throw new InvalidOperationException($"Expected {typeof(T).Name}.");}
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
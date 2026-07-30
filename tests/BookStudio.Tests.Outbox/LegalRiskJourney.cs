using System.Security.Cryptography;
using System.Text;
using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;
using BookStudio.Infrastructure.Persistence.Sqlite.Research;

namespace BookStudio.Tests.Outbox;

internal static class LegalRiskJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options=SqliteWorkspaceOptions.Create(workspaceRoot,"legal-risk.db",64);
        await using var database=new SqliteWorkspaceDatabase(options);
        var health=await database.InitializeAsync();
        Require(health.LatestMigrationVersion>=40,"Legal risk migration missing.");
        var factory=new SqliteConnectionFactory(options);
        var now=new DateTimeOffset(2026,7,30,21,0,0,TimeSpan.Zero);
        const string workspace="workspace-a";
        var project=Guid.NewGuid(); var provenance=Guid.NewGuid(); var subject=Guid.NewGuid(); var digest=Hash("subject-v1"); const long authorityRevision=3;
        var authorityDigest=Hash($"{workspace}:{provenance:D}:{authorityRevision}:APPROVED");
        SeedProvenance(factory,workspace,project,provenance,subject,digest,authorityRevision,now);
        var caseId=Guid.NewGuid(); Guid approvedMessage;
        await using(var store=new SqliteLegalRiskStore(factory))
        {
            var draft=new LegalRiskDraft(caseId,project,workspace,provenance,authorityRevision,authorityDigest,subject,"chapter-4",digest,1,new[]{"ES","FR"},"legal-policy-2026.1","legal-coordinator","{\"subject\":\"chapter-4\"}","create-legal-risk");
            var created=await store.CreateAsync(draft,now.AddMinutes(1));
            Require(!created.Replayed&&created.Case.Status==LegalRiskStatus.Proposed&&created.Case.Revision==1,"Create failed.");
            Require((await store.CreateAsync(draft,now.AddMinutes(2))).Replayed,"Create replay failed.");
            await Throws<LegalRiskConflictException>(()=>store.CreateAsync(draft with{Actor="other"},now.AddMinutes(2)).AsTask());
            var finding=new LegalRiskFindingDraft(Guid.NewGuid(),LegalRiskCategory.Defamation,"chapter-4:p12","Named individual","ES",LegalRiskSeverity.High,0.68m,"Unverified allegation may harm reputation.","Claim and source review retained.","Remove identifying details or obtain substantiation.",true);
            var eval=new LegalRiskEvaluateCommand(Guid.NewGuid(),workspace,caseId,1,new[]{finding},"Policy matrix and cited passage retained.","legal-coordinator","evaluate-legal-risk");
            var evaluated=await store.EvaluateAsync(eval,now.AddMinutes(3));
            Require(evaluated.Status==LegalRiskStatus.HumanReviewRequired&&evaluated.Revision==2&&evaluated.Findings.Count==1,"Human routing failed.");
            await Throws<LegalRiskTransitionException>(()=>store.DecideAsync(new(Guid.NewGuid(),workspace,caseId,2,LegalRiskDecision.Approve,"Automated approval attempted.","automation","invalid-auto-approve"),now.AddMinutes(4)).AsTask());
            var review=new LegalRiskHumanReviewCommand(Guid.NewGuid(),workspace,caseId,2,Guid.NewGuid(),"reviewer@example.test","Qualified Legal Counsel","Defamation and privacy review",LegalHumanDecision.ApproveWithConditions,"Publication allowed only after anonymisation.","Signed legal review memorandum.","Replace personal name and unique identifiers.",now.AddYears(1),"legal-coordinator","human-review");
            var reviewed=await store.RecordHumanReviewAsync(review,now.AddMinutes(5));
            Require(reviewed.Status==LegalRiskStatus.Evaluated&&reviewed.Revision==3&&reviewed.Reviews.Count==1,"Human review failed.");
            var approve=new LegalRiskDecisionCommand(Guid.NewGuid(),workspace,caseId,3,LegalRiskDecision.Approve,"Qualified review and conditions accepted.","legal-coordinator","approve-legal-risk");
            var approved=await store.DecideAsync(approve,now.AddMinutes(6));
            Require(approved.Status==LegalRiskStatus.Approved&&approved.Revision==4,"Approval failed.");
            approvedMessage=approved.MessageId??throw new InvalidOperationException("Approved event missing.");
            Require((await store.DecideAsync(approve,now.AddMinutes(7))).Revision==4,"Decision replay changed state.");
            Require(await store.GetAsync("workspace-b",caseId) is null,"Workspace isolation failed.");
        }
        await using(var restarted=new SqliteLegalRiskStore(factory))
        {
            var durable=await restarted.GetAsync(workspace,caseId)??throw new InvalidOperationException("Case missing after restart.");
            Require(durable.Status==LegalRiskStatus.Approved&&durable.Revision==4&&durable.Findings.Count==1&&durable.Reviews.Count==1,"Restart durability failed.");
        }
        using(var c=factory.OpenConnection())
        {
            using var cmd=c.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM legal_risk_history WHERE workspace_id=$w AND case_id=$id";cmd.Parameters.AddWithValue("$w",workspace);cmd.Parameters.AddWithValue("$id",caseId.ToString("D"));Require(Convert.ToInt32(cmd.ExecuteScalar())==4,"History is not append-only exactly once.");
        }
        await using var outbox=new SqliteOutboxStore(factory);
        var messages=await outbox.ClaimAsync("legal-risk-worker",100,TimeSpan.FromMinutes(5),now.AddHours(1));
        Require(messages.Count(x=>x.MessageId==approvedMessage&&x.EventType=="legal.risk.approved")==1,"Approved event was not exactly once.");
    }

    private static void SeedProvenance(SqliteConnectionFactory factory,string workspace,Guid project,Guid record,Guid subject,string digest,long revision,DateTimeOffset at)
    {
        using var c=factory.OpenConnection();using var cmd=c.CreateCommand();
        cmd.CommandText="INSERT INTO ai_provenance_records(workspace_id,record_id,project_id,rights_license_case_id,expected_rights_revision,expected_rights_digest,asset_id,asset_kind,asset_reference,asset_digest,asset_version,actor,snapshot_json,revision,status,classification,provider,model,model_version,prompt_reference,human_transformations,ai_contribution_percent,evidence,decision,decision_reason,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$rights,3,'rights-digest',$asset,'MANUSCRIPT','chapter-4',$digest,1,'provenance-editor','{}',$r,'APPROVED','AI_ASSISTED','OpenAI','text-model','2026-07','prompt://chapter-4','Human revision',25,'Evidence retained','APPROVE','Approved provenance',NULL,$at,$at)";
        cmd.Parameters.AddWithValue("$w",workspace);cmd.Parameters.AddWithValue("$id",record.ToString("D"));cmd.Parameters.AddWithValue("$p",project.ToString("D"));cmd.Parameters.AddWithValue("$rights",Guid.NewGuid().ToString("D"));cmd.Parameters.AddWithValue("$asset",subject.ToString("D"));cmd.Parameters.AddWithValue("$digest",digest);cmd.Parameters.AddWithValue("$r",revision);cmd.Parameters.AddWithValue("$at",at.ToString("O"));cmd.ExecuteNonQuery();
    }
    private static async Task Throws<T>(Func<Task> action) where T:Exception { try{await action();}catch(T){return;}throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}

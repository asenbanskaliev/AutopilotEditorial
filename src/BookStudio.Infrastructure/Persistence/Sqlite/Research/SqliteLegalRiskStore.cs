using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Research;

public sealed class SqliteLegalRiskStore : ILegalRiskStore, IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, LegalRiskCase> Cases = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> Receipts = new(StringComparer.Ordinal);
    private readonly SqliteConnectionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteLegalRiskStore(SqliteConnectionFactory factory, int capacity = 64) => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async ValueTask<LegalRiskCreateResult> CreateAsync(LegalRiskDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(d); await _gate.WaitAsync(ct); try
        {
            var key = Key(d.WorkspaceId,d.CaseId); var payload = Hash(JsonSerializer.Serialize(d));
            if (Cases.TryGetValue(key,out var existing)) { RequireReceipt(d.WorkspaceId,d.CaseId,d.CaseId,"CREATE",d.RequestFingerprint,payload); return new(existing,true); }
            RequireAuthority(d);
            var message=MessageId(d.CaseId);
            var item=new LegalRiskCase(d.CaseId,d.ProjectId,d.WorkspaceId,d.ProvenanceRecordId,d.ExpectedProvenanceRevision,d.ExpectedProvenanceDigest,d.SubjectId,d.SubjectReference,d.SubjectDigest,d.SubjectVersion,d.Jurisdictions,d.PolicyVersion,1,LegalRiskStatus.Proposed,[],[],null,null,null,message,at,at);
            PersistCreate(d,item,payload,at); Cases[key]=item; return new(item,false);
        } finally { _gate.Release(); }
    }

    public async ValueTask<LegalRiskCase> EvaluateAsync(LegalRiskEvaluateCommand c, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateEvaluation(c); return await Mutate(c.RequestId,c.WorkspaceId,c.CaseId,c.ExpectedRevision,"EVALUATE",c.RequestFingerprint,Hash(JsonSerializer.Serialize(c)),at,item =>
        {
            if (!AuthorityMatches(item)) return item with { Revision=item.Revision+1,Status=LegalRiskStatus.Stale,DecisionReason="Provenance authority drift.",MessageId=MessageId(c.RequestId),UpdatedAtUtc=at };
            if (item.Status is not (LegalRiskStatus.Proposed or LegalRiskStatus.RepairRequired)) throw new LegalRiskTransitionException("Case cannot be evaluated.");
            var findings=c.Findings.Select(x=>new LegalRiskFinding(x.FindingId,x.Category,x.Citation,x.AffectedParty,x.Jurisdiction,x.Severity,x.Confidence,x.Rationale,x.Evidence,x.ProposedMitigation,x.PolicyMandatesHumanReview,false)).ToArray();
            var human=c.Findings.Any(NeedsHuman);
            return item with { Revision=item.Revision+1,Status=human?LegalRiskStatus.HumanReviewRequired:LegalRiskStatus.Evaluated,Findings=findings,Evidence=c.Evidence,Decision=null,DecisionReason=null,MessageId=MessageId(c.RequestId),UpdatedAtUtc=at };
        },c.Actor,c.Evidence,ct);
    }

    public async ValueTask<LegalRiskCase> RecordHumanReviewAsync(LegalRiskHumanReviewCommand c, DateTimeOffset at, CancellationToken ct = default)
    {
        if(c.ReviewId==Guid.Empty||string.IsNullOrWhiteSpace(c.ReviewerIdentity)||string.IsNullOrWhiteSpace(c.ReviewerRole)||!c.ReviewerRole.Contains("legal",StringComparison.OrdinalIgnoreCase)||string.IsNullOrWhiteSpace(c.Scope)||string.IsNullOrWhiteSpace(c.Rationale)||string.IsNullOrWhiteSpace(c.Evidence)) throw new LegalRiskValidationException("Qualified human legal review is required.");
        return await Mutate(c.RequestId,c.WorkspaceId,c.CaseId,c.ExpectedRevision,"HUMAN_REVIEW",c.RequestFingerprint,Hash(JsonSerializer.Serialize(c)),at,item =>
        {
            if(item.Status!=LegalRiskStatus.HumanReviewRequired) throw new LegalRiskTransitionException("Human review is not expected.");
            var review=new LegalRiskHumanReview(c.ReviewId,c.ReviewerIdentity,c.ReviewerRole,c.Scope,c.Decision,c.Rationale,c.Evidence,c.Conditions,c.ExpiresAtUtc,at);
            var status=c.Decision switch { LegalHumanDecision.Reject=>LegalRiskStatus.Blocked,LegalHumanDecision.RequireRepair=>LegalRiskStatus.RepairRequired,_=>LegalRiskStatus.Evaluated };
            return item with { Revision=item.Revision+1,Status=status,Reviews=item.Reviews.Append(review).ToArray(),MessageId=MessageId(c.RequestId),UpdatedAtUtc=at };
        },c.Actor,c.Rationale,ct);
    }

    public async ValueTask<LegalRiskCase> DecideAsync(LegalRiskDecisionCommand c, DateTimeOffset at, CancellationToken ct = default)
    {
        if(string.IsNullOrWhiteSpace(c.Reason)) throw new LegalRiskValidationException("Decision reason is required.");
        return await Mutate(c.RequestId,c.WorkspaceId,c.CaseId,c.ExpectedRevision,"DECIDE",c.RequestFingerprint,Hash(JsonSerializer.Serialize(c)),at,item =>
        {
            var status=c.Decision switch
            {
                LegalRiskDecision.Approve when item.Status==LegalRiskStatus.Evaluated && HasValidReview(item,at)=>LegalRiskStatus.Approved,
                LegalRiskDecision.Approve when item.Status==LegalRiskStatus.Evaluated && !item.Findings.Any(NeedsHuman)=>LegalRiskStatus.Approved,
                LegalRiskDecision.Block when item.Status is LegalRiskStatus.Evaluated or LegalRiskStatus.HumanReviewRequired=>LegalRiskStatus.Blocked,
                LegalRiskDecision.ReturnToRepair when item.Status is LegalRiskStatus.Evaluated or LegalRiskStatus.HumanReviewRequired=>LegalRiskStatus.RepairRequired,
                LegalRiskDecision.Revoke when item.Status==LegalRiskStatus.Approved=>LegalRiskStatus.Revoked,
                _=>throw new LegalRiskTransitionException("Decision is not valid for current state.")
            };
            return item with { Revision=item.Revision+1,Status=status,Decision=c.Decision,DecisionReason=c.Reason,MessageId=MessageId(c.RequestId),UpdatedAtUtc=at };
        },c.Actor,c.Reason,ct);
    }

    public ValueTask<LegalRiskCase> ReopenAsync(LegalRiskReopenCommand c, DateTimeOffset at, CancellationToken ct = default) => Mutate(c.RequestId,c.WorkspaceId,c.CaseId,c.ExpectedRevision,"REOPEN",c.RequestFingerprint,Hash(JsonSerializer.Serialize(c)),at,item => item.Status is LegalRiskStatus.Approved or LegalRiskStatus.Blocked or LegalRiskStatus.RepairRequired or LegalRiskStatus.Revoked ? item with { Revision=item.Revision+1,Status=LegalRiskStatus.Proposed,Findings=[],Reviews=[],Evidence=null,Decision=null,DecisionReason=c.Reason,MessageId=MessageId(c.RequestId),UpdatedAtUtc=at } : throw new LegalRiskTransitionException("Case cannot be reopened."),c.Actor,c.Reason,ct);
    public ValueTask<LegalRiskCase> MarkStaleAsync(LegalRiskStaleCommand c, DateTimeOffset at, CancellationToken ct = default) => Mutate(c.RequestId,c.WorkspaceId,c.CaseId,c.ExpectedRevision,"STALE",c.RequestFingerprint,Hash(JsonSerializer.Serialize(c)),at,item => item with { Revision=item.Revision+1,Status=LegalRiskStatus.Stale,DecisionReason=c.Reason,MessageId=MessageId(c.RequestId),UpdatedAtUtc=at },c.Actor,c.Reason,ct);
    public ValueTask<LegalRiskCase?> GetAsync(string workspaceId,Guid caseId,CancellationToken ct=default){ct.ThrowIfCancellationRequested();Cases.TryGetValue(Key(workspaceId,caseId),out var item);return ValueTask.FromResult(item);}

    private async ValueTask<LegalRiskCase> Mutate(Guid request,string workspace,Guid id,long expected,string op,string fingerprint,string payload,DateTimeOffset at,Func<LegalRiskCase,LegalRiskCase> mutation,string actor,string reason,CancellationToken ct)
    {
        await _gate.WaitAsync(ct); try
        {
            var receiptKey=ReceiptKey(workspace,request); if(Receipts.TryGetValue(receiptKey,out var old)){if(old!=$"{op}|{id:D}|{fingerprint}|{payload}")throw new LegalRiskConflictException("Request reused with different payload.");return Require(workspace,id);}
            var item=Require(workspace,id); if(item.Revision!=expected)throw new LegalRiskConflictException("Stale revision."); var next=mutation(item);
            PersistTransition(next,op,actor,reason,at); Cases[Key(workspace,id)]=next; Receipts[receiptKey]=$"{op}|{id:D}|{fingerprint}|{payload}"; return next;
        } finally { _gate.Release(); }
    }

    private void PersistCreate(LegalRiskDraft d,LegalRiskCase item,string payload,DateTimeOffset at)
    {
        using var c=_factory.OpenConnection();using var tx=c.BeginTransaction();
        Exec(c,tx,"INSERT INTO legal_risk_cases(workspace_id,case_id,project_id,provenance_record_id,expected_provenance_revision,expected_provenance_digest,subject_id,subject_reference,subject_digest,subject_version,jurisdictions_json,policy_version,snapshot_json,revision,status,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,$ar,$ad,$s,$sr,$sd,$sv,$j,$pv,$snap,1,'PROPOSED',$m,$at,$at)",("$w",d.WorkspaceId),("$id",d.CaseId.ToString("D")),("$p",d.ProjectId.ToString("D")),("$a",d.ProvenanceRecordId.ToString("D")),("$ar",d.ExpectedProvenanceRevision),("$ad",d.ExpectedProvenanceDigest),("$s",d.SubjectId.ToString("D")),("$sr",d.SubjectReference),("$sd",d.SubjectDigest),("$sv",d.SubjectVersion),("$j",JsonSerializer.Serialize(d.Jurisdictions)),("$pv",d.PolicyVersion),("$snap",d.SnapshotJson),("$m",item.MessageId!.Value.ToString("D")),("$at",at.ToString("O")));
        History(c,tx,item,"CREATE",d.Actor,null,at); Outbox(c,tx,item,"legal.risk.proposed",at); tx.Commit(); Receipts[ReceiptKey(d.WorkspaceId,d.CaseId)]=$"CREATE|{d.CaseId:D}|{d.RequestFingerprint}|{payload}";
    }
    private void PersistTransition(LegalRiskCase item,string op,string actor,string reason,DateTimeOffset at)
    {
        using var c=_factory.OpenConnection();using var tx=c.BeginTransaction();
        Exec(c,tx,"UPDATE legal_risk_cases SET revision=$r,status=$s,evidence=$e,decision=$d,decision_reason=$reason,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND case_id=$id",("$r",item.Revision),("$s",item.Status.ToString().ToUpperInvariant()),("$e",Db(item.Evidence)),("$d",Db(item.Decision?.ToString().ToUpperInvariant())),("$reason",Db(item.DecisionReason)),("$m",item.MessageId!.Value.ToString("D")),("$at",at.ToString("O")),("$w",item.WorkspaceId),("$id",item.CaseId.ToString("D")));
        History(c,tx,item,op,actor,reason,at); Outbox(c,tx,item,$"legal.risk.{item.Status.ToString().ToLowerInvariant()}",at); tx.Commit();
    }
    private void RequireAuthority(LegalRiskDraft d){using var c=_factory.OpenConnection();using var cmd=c.CreateCommand();cmd.CommandText="SELECT project_id,revision,status,asset_id,asset_digest,asset_version FROM ai_provenance_records WHERE workspace_id=$w AND record_id=$id";cmd.Parameters.AddWithValue("$w",d.WorkspaceId);cmd.Parameters.AddWithValue("$id",d.ProvenanceRecordId.ToString("D"));using var r=cmd.ExecuteReader();if(!r.Read())throw new LegalRiskValidationException("Approved provenance authority not found.");var status=r.GetString(2);var digest=Hash($"{d.WorkspaceId}:{d.ProvenanceRecordId:D}:{r.GetInt64(1)}:{status}");if(Guid.Parse(r.GetString(0))!=d.ProjectId||r.GetInt64(1)!=d.ExpectedProvenanceRevision||status!="APPROVED"||digest!=d.ExpectedProvenanceDigest||Guid.Parse(r.GetString(3))!=d.SubjectId||r.GetString(4)!=d.SubjectDigest||r.GetInt32(5)!=d.SubjectVersion)throw new LegalRiskValidationException("Provenance authority is not exact and approved.");}
    private bool AuthorityMatches(LegalRiskCase i){try{RequireAuthority(new(i.CaseId,i.ProjectId,i.WorkspaceId,i.ProvenanceRecordId,i.ExpectedProvenanceRevision,i.ExpectedProvenanceDigest,i.SubjectId,i.SubjectReference,i.SubjectDigest,i.SubjectVersion,i.Jurisdictions,i.PolicyVersion,"system","{}","check"));return true;}catch(LegalRiskValidationException){return false;}}
    private static bool NeedsHuman(LegalRiskFindingDraft f)=>f.PolicyMandatesHumanReview||f.Severity is LegalRiskSeverity.High or LegalRiskSeverity.Critical or LegalRiskSeverity.Unknown||f.Confidence<0.75m;
    private static bool NeedsHuman(LegalRiskFinding f)=>f.PolicyMandatesHumanReview||f.Severity is LegalRiskSeverity.High or LegalRiskSeverity.Critical or LegalRiskSeverity.Unknown||f.Confidence<0.75m;
    private static bool HasValidReview(LegalRiskCase i,DateTimeOffset at)=>i.Reviews.Any(r=>r.Decision is LegalHumanDecision.Approve or LegalHumanDecision.ApproveWithConditions&&(r.ExpiresAtUtc is null||r.ExpiresAtUtc>at));
    private static void ValidateDraft(LegalRiskDraft d){if(d.CaseId==Guid.Empty||d.ProjectId==Guid.Empty||d.ProvenanceRecordId==Guid.Empty||d.SubjectId==Guid.Empty||d.ExpectedProvenanceRevision<1||d.SubjectVersion<1||d.Jurisdictions.Count==0||string.IsNullOrWhiteSpace(d.WorkspaceId)||string.IsNullOrWhiteSpace(d.ExpectedProvenanceDigest)||string.IsNullOrWhiteSpace(d.SubjectDigest)||string.IsNullOrWhiteSpace(d.PolicyVersion)||string.IsNullOrWhiteSpace(d.Actor)||string.IsNullOrWhiteSpace(d.RequestFingerprint))throw new LegalRiskValidationException("Complete draft is required.");}
    private static void ValidateEvaluation(LegalRiskEvaluateCommand c){if(string.IsNullOrWhiteSpace(c.Evidence)||c.Findings.Any(f=>f.FindingId==Guid.Empty||string.IsNullOrWhiteSpace(f.Citation)||string.IsNullOrWhiteSpace(f.Jurisdiction)||f.Confidence<0||f.Confidence>1||string.IsNullOrWhiteSpace(f.Evidence)))throw new LegalRiskValidationException("Complete findings are required.");}
    private LegalRiskCase Require(string w,Guid id)=>Cases.TryGetValue(Key(w,id),out var i)?i:throw new LegalRiskValidationException("Legal risk case not found.");
    private static void RequireReceipt(string w,Guid request,Guid id,string op,string fingerprint,string payload){if(!Receipts.TryGetValue(ReceiptKey(w,request),out var v)||v!=$"{op}|{id:D}|{fingerprint}|{payload}")throw new LegalRiskConflictException("Request reused with different payload.");}
    private static void History(SqliteConnection c,SqliteTransaction tx,LegalRiskCase i,string op,string actor,string? reason,DateTimeOffset at)=>Exec(c,tx,"INSERT INTO legal_risk_history(workspace_id,case_id,revision,transition,actor,reason,payload_json,occurred_at_utc) VALUES($w,$id,$r,$o,$a,$reason,$p,$at)",("$w",i.WorkspaceId),("$id",i.CaseId.ToString("D")),("$r",i.Revision),("$o",op),("$a",actor),("$reason",Db(reason)),("$p",JsonSerializer.Serialize(new{i.Status,i.Decision})),("$at",at.ToString("O")));
    private static void Outbox(SqliteConnection c,SqliteTransaction tx,LegalRiskCase i,string type,DateTimeOffset at)=>Exec(c,tx,"INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,created_at_utc) VALUES($id,$t,'1',$p,$at,$at,'PENDING',0,$at)",("$id",i.MessageId!.Value.ToString("D")),("$t",type),("$p",JsonSerializer.Serialize(new{i.WorkspaceId,i.CaseId,i.SubjectId})),("$at",at.ToString("O")));
    private static void Exec(SqliteConnection c,SqliteTransaction tx,string sql,params (string,object)[] values){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var v in values)cmd.Parameters.AddWithValue(v.Item1,v.Item2);cmd.ExecuteNonQuery();}
    private static object Db(string? v)=>v is null?DBNull.Value:v;
    private static string Key(string w,Guid id)=>w+":"+id.ToString("D"); private static string ReceiptKey(string w,Guid id)=>w+":"+id.ToString("D");
    private static Guid MessageId(Guid id){var b=SHA256.HashData(Encoding.UTF8.GetBytes("legal-risk:"+id.ToString("D")));return new Guid(b[..16]);}
    private static string Hash(string v)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(v))).ToLowerInvariant();
    public ValueTask DisposeAsync(){_gate.Dispose();return ValueTask.CompletedTask;}
}

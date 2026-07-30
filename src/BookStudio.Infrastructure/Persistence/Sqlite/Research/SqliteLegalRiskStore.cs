using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Research;

public sealed class SqliteLegalRiskStore : ILegalRiskStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteLegalRiskStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<LegalRiskCreateResult> CreateAsync(LegalRiskDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(d);
        var hash = Hash(JsonSerializer.Serialize(d));
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, d.WorkspaceId, d.CaseId);
            if (existing is not null)
            {
                RequireReceipt(ReadReceipt(c, tx, d.WorkspaceId, d.CaseId), "CREATE", d.CaseId, d.RequestFingerprint, hash);
                return new LegalRiskCreateResult(existing, true);
            }
            RequireAuthority(c, tx, d);
            var message = MessageId(d.CaseId);
            Execute(c, tx, "INSERT INTO legal_risk_cases(workspace_id,case_id,project_id,provenance_record_id,expected_provenance_revision,expected_provenance_digest,subject_id,subject_reference,subject_digest,subject_version,jurisdictions_json,policy_version,snapshot_json,revision,status,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,$ar,$ad,$s,$sr,$sd,$sv,$j,$pv,$snap,1,'PROPOSED',$m,$at,$at)",
                ("$w",d.WorkspaceId),("$id",d.CaseId.ToString("D")),("$p",d.ProjectId.ToString("D")),("$a",d.ProvenanceRecordId.ToString("D")),("$ar",d.ExpectedProvenanceRevision),("$ad",d.ExpectedProvenanceDigest),("$s",d.SubjectId.ToString("D")),("$sr",d.SubjectReference),("$sd",d.SubjectDigest),("$sv",d.SubjectVersion),("$j",JsonSerializer.Serialize(d.Jurisdictions)),("$pv",d.PolicyVersion),("$snap",d.SnapshotJson),("$m",message.ToString("D")),("$at",Text(at)));
            History(c,tx,d.WorkspaceId,d.CaseId,1,"CREATE",d.Actor,null,new { d.SubjectId,d.Jurisdictions,d.PolicyVersion },at);
            Receipt(c,tx,d.WorkspaceId,d.CaseId,d.CaseId,"CREATE",d.RequestFingerprint,hash,1,message,at);
            Outbox(c,tx,message,"legal.risk.proposed",new { d.WorkspaceId,d.CaseId,d.SubjectId },at);
            return new LegalRiskCreateResult(Require(c,tx,d.WorkspaceId,d.CaseId),false);
        },ct);
    }

    public ValueTask<LegalRiskCase> EvaluateAsync(LegalRiskEvaluateCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateEvaluation(cmd);
        return Mutate(cmd.RequestId,cmd.WorkspaceId,cmd.CaseId,"EVALUATE",cmd.RequestFingerprint,Hash(JsonSerializer.Serialize(cmd)),cmd.ExpectedRevision,at,(c,tx,item) =>
        {
            if (!AuthorityMatches(c,tx,item)) return Advance(c,tx,item,LegalRiskStatus.Stale,cmd.RequestId,"STALE",cmd.Actor,"Provenance authority drift.","legal.risk.stale",at);
            if (item.Status is not (LegalRiskStatus.Proposed or LegalRiskStatus.RepairRequired)) throw new LegalRiskTransitionException("Case cannot be evaluated from its current state.");
            Execute(c,tx,"DELETE FROM legal_risk_findings WHERE workspace_id=$w AND case_id=$id",("$w",item.WorkspaceId),("$id",item.CaseId.ToString("D")));
            foreach (var f in cmd.Findings) Execute(c,tx,"INSERT INTO legal_risk_findings(workspace_id,case_id,finding_id,category,citation,affected_party,jurisdiction,severity,confidence,rationale,evidence,proposed_mitigation,policy_mandates_human_review,resolved) VALUES($w,$c,$id,$cat,$cite,$party,$jur,$sev,$conf,$rat,$ev,$mit,$human,0)",
                ("$w",item.WorkspaceId),("$c",item.CaseId.ToString("D")),("$id",f.FindingId.ToString("D")),("$cat",f.Category.ToString().ToUpperInvariant()),("$cite",f.Citation),("$party",f.AffectedParty),("$jur",f.Jurisdiction),("$sev",f.Severity.ToString().ToUpperInvariant()),("$conf",f.Confidence),("$rat",f.Rationale),("$ev",f.Evidence),("$mit",f.ProposedMitigation),("$human",f.PolicyMandatesHumanReview?1:0));
            var human = cmd.Findings.Any(NeedsHuman);
            return Advance(c,tx,item,human?LegalRiskStatus.HumanReviewRequired:LegalRiskStatus.Evaluated,cmd.RequestId,"EVALUATE",cmd.Actor,cmd.Evidence,human?"legal.risk.human_review_required":"legal.risk.evaluated",at,evidence:cmd.Evidence);
        },ct);
    }

    public ValueTask<LegalRiskCase> RecordHumanReviewAsync(LegalRiskHumanReviewCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (cmd.ReviewId==Guid.Empty || string.IsNullOrWhiteSpace(cmd.ReviewerIdentity) || string.IsNullOrWhiteSpace(cmd.ReviewerRole) || !cmd.ReviewerRole.Contains("legal",StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(cmd.Scope) || string.IsNullOrWhiteSpace(cmd.Rationale) || string.IsNullOrWhiteSpace(cmd.Evidence)) throw new LegalRiskValidationException("Qualified human legal review evidence is required.");
        return Mutate(cmd.RequestId,cmd.WorkspaceId,cmd.CaseId,"HUMAN_REVIEW",cmd.RequestFingerprint,Hash(JsonSerializer.Serialize(cmd)),cmd.ExpectedRevision,at,(c,tx,item) =>
        {
            if (item.Status!=LegalRiskStatus.HumanReviewRequired) throw new LegalRiskTransitionException("Human review is not expected.");
            Execute(c,tx,"INSERT INTO legal_risk_reviews(workspace_id,case_id,review_id,reviewer_identity,reviewer_role,scope,decision,rationale,evidence,conditions,expires_at_utc,reviewed_at_utc) VALUES($w,$c,$id,$ri,$rr,$scope,$d,$rat,$ev,$cond,$exp,$at)",
                ("$w",item.WorkspaceId),("$c",item.CaseId.ToString("D")),("$id",cmd.ReviewId.ToString("D")),("$ri",cmd.ReviewerIdentity),("$rr",cmd.ReviewerRole),("$scope",cmd.Scope),("$d",cmd.Decision.ToString().ToUpperInvariant()),("$rat",cmd.Rationale),("$ev",cmd.Evidence),("$cond",(object?)cmd.Conditions??DBNull.Value),("$exp",cmd.ExpiresAtUtc is null?DBNull.Value:Text(cmd.ExpiresAtUtc.Value)),("$at",Text(at)));
            var status = cmd.Decision switch { LegalHumanDecision.Reject=>LegalRiskStatus.Blocked, LegalHumanDecision.RequireRepair=>LegalRiskStatus.RepairRequired, _=>LegalRiskStatus.Evaluated };
            return Advance(c,tx,item,status,cmd.RequestId,"HUMAN_REVIEW",cmd.Actor,cmd.Rationale,"legal.risk.human_reviewed",at);
        },ct);
    }

    public ValueTask<LegalRiskCase> DecideAsync(LegalRiskDecisionCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new LegalRiskValidationException("Decision reason is required.");
        return Mutate(cmd.RequestId,cmd.WorkspaceId,cmd.CaseId,"DECIDE",cmd.RequestFingerprint,Hash(JsonSerializer.Serialize(cmd)),cmd.ExpectedRevision,at,(c,tx,item) =>
        {
            if (!AuthorityMatches(c,tx,item)) return Advance(c,tx,item,LegalRiskStatus.Stale,cmd.RequestId,"STALE",cmd.Actor,"Provenance authority drift.","legal.risk.stale",at);
            var status = cmd.Decision switch
            {
                LegalRiskDecision.Approve when item.Status==LegalRiskStatus.Evaluated && !HasBlockingFindings(item,at)=>LegalRiskStatus.Approved,
                LegalRiskDecision.Block when item.Status is LegalRiskStatus.Evaluated or LegalRiskStatus.HumanReviewRequired=>LegalRiskStatus.Blocked,
                LegalRiskDecision.ReturnToRepair when item.Status is LegalRiskStatus.Evaluated or LegalRiskStatus.HumanReviewRequired=>LegalRiskStatus.RepairRequired,
                LegalRiskDecision.Revoke when item.Status==LegalRiskStatus.Approved=>LegalRiskStatus.Revoked,
                _=>throw new LegalRiskTransitionException("Decision is not valid for current state.")
            };
            return Advance(c,tx,item,status,cmd.RequestId,"DECIDE",cmd.Actor,cmd.Reason,$"legal.risk.{status.ToString().ToLowerInvariant()}",at,decision:cmd.Decision);
        },ct);
    }

    public ValueTask<LegalRiskCase> ReopenAsync(LegalRiskReopenCommand cmd, DateTimeOffset at, CancellationToken ct = default) => Mutate(cmd.RequestId,cmd.WorkspaceId,cmd.CaseId,"REOPEN",cmd.RequestFingerprint,Hash(JsonSerializer.Serialize(cmd)),cmd.ExpectedRevision,at,(c,tx,item) =>
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new LegalRiskValidationException("Reopen reason is required.");
        if (item.Status is LegalRiskStatus.Proposed or LegalRiskStatus.HumanReviewRequired or LegalRiskStatus.Evaluated or LegalRiskStatus.Stale) throw new LegalRiskTransitionException("Case cannot be reopened.");
        Execute(c,tx,"DELETE FROM legal_risk_findings WHERE workspace_id=$w AND case_id=$id",("$w",item.WorkspaceId),("$id",item.CaseId.ToString("D")));
        Execute(c,tx,"DELETE FROM legal_risk_reviews WHERE workspace_id=$w AND case_id=$id",("$w",item.WorkspaceId),("$id",item.CaseId.ToString("D")));
        return Advance(c,tx,item,LegalRiskStatus.Proposed,cmd.RequestId,"REOPEN",cmd.Actor,cmd.Reason,"legal.risk.reopened",at,clear:true);
    },ct);

    public ValueTask<LegalRiskCase> MarkStaleAsync(LegalRiskStaleCommand cmd, DateTimeOffset at, CancellationToken ct = default) => Mutate(cmd.RequestId,cmd.WorkspaceId,cmd.CaseId,"STALE",cmd.RequestFingerprint,Hash(JsonSerializer.Serialize(cmd)),cmd.ExpectedRevision,at,(c,tx,item) =>
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new LegalRiskValidationException("Stale reason is required.");
        return Advance(c,tx,item,LegalRiskStatus.Stale,cmd.RequestId,"STALE",cmd.Actor,cmd.Reason,"legal.risk.stale",at);
    },ct);

    public ValueTask<LegalRiskCase?> GetAsync(string workspaceId, Guid caseId, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); using var c=_factory.OpenConnection(); return ValueTask.FromResult(Read(c,null,workspaceId,caseId)); }
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed,1)==0) await _queue.DisposeAsync().ConfigureAwait(false); }

    private ValueTask<LegalRiskCase> Mutate(Guid request,string workspace,Guid id,string operation,string fingerprint,string hash,long expected,DateTimeOffset at,Func<SqliteConnection,SqliteTransaction,LegalRiskCase,LegalRiskCase> action,CancellationToken ct) => _queue.ExecuteInTransactionAsync((c,tx,token)=> { token.ThrowIfCancellationRequested(); var r=ReadReceipt(c,tx,workspace,request); if(r is not null){RequireReceipt(r,operation,id,fingerprint,hash);return Require(c,tx,workspace,id);} var item=Require(c,tx,workspace,id); if(item.Revision!=expected) throw new LegalRiskConflictException("Stale revision."); var result=action(c,tx,item); Receipt(c,tx,workspace,request,id,operation,fingerprint,hash,result.Revision,result.MessageId,at); return result; },ct);

    private static LegalRiskCase Advance(SqliteConnection c,SqliteTransaction tx,LegalRiskCase item,LegalRiskStatus status,Guid request,string action,string actor,string reason,string eventType,DateTimeOffset at,string? evidence=null,LegalRiskDecision? decision=null,bool clear=false)
    {
        var revision=item.Revision+1; var message=MessageId(request);
        Execute(c,tx,"UPDATE legal_risk_cases SET revision=$r,status=$s,evidence=$e,decision=$d,decision_reason=$reason,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND case_id=$id",
            ("$r",revision),("$s",status.ToString().ToUpperInvariant()),("$e",clear?DBNull.Value:(object?)evidence??item.Evidence??DBNull.Value),("$d",decision is null?DBNull.Value:decision.Value.ToString().ToUpperInvariant()),("$reason",reason),("$m",message.ToString("D")),("$at",Text(at)),("$w",item.WorkspaceId),("$id",item.CaseId.ToString("D")));
        History(c,tx,item.WorkspaceId,item.CaseId,revision,action,actor,reason,new { status,decision },at); Outbox(c,tx,message,eventType,new { item.WorkspaceId,item.CaseId,item.SubjectId,Actor=actor },at); return Require(c,tx,item.WorkspaceId,item.CaseId);
    }

    private static bool NeedsHuman(LegalRiskFindingDraft f) => f.PolicyMandatesHumanReview || f.Severity is LegalRiskSeverity.High or LegalRiskSeverity.Critical or LegalRiskSeverity.Unknown || f.Confidence<0.75m;
    private static bool HasBlockingFindings(LegalRiskCase item,DateTimeOffset at)
    {
        var needsHuman=item.Findings.Any(f=>!f.Resolved && (f.PolicyMandatesHumanReview || f.Severity is LegalRiskSeverity.High or LegalRiskSeverity.Critical or LegalRiskSeverity.Unknown || f.Confidence<0.75m));
        if(!needsHuman) return item.Findings.Any(f=>!f.Resolved && f.Severity==LegalRiskSeverity.Critical);
        return !item.Reviews.Any(r=>r.Decision is LegalHumanDecision.Approve or LegalHumanDecision.ApproveWithConditions && (r.ExpiresAtUtc is null || r.ExpiresAtUtc>at));
    }

    private static void ValidateDraft(LegalRiskDraft d) { if(d.CaseId==Guid.Empty||d.ProjectId==Guid.Empty||d.ProvenanceRecordId==Guid.Empty||d.SubjectId==Guid.Empty||d.ExpectedProvenanceRevision<1||d.SubjectVersion<1||d.Jurisdictions.Count==0||d.Jurisdictions.Any(string.IsNullOrWhiteSpace)||string.IsNullOrWhiteSpace(d.WorkspaceId)||string.IsNullOrWhiteSpace(d.ExpectedProvenanceDigest)||string.IsNullOrWhiteSpace(d.SubjectReference)||string.IsNullOrWhiteSpace(d.SubjectDigest)||string.IsNullOrWhiteSpace(d.PolicyVersion)||string.IsNullOrWhiteSpace(d.Actor)||string.IsNullOrWhiteSpace(d.SnapshotJson)||string.IsNullOrWhiteSpace(d.RequestFingerprint)) throw new LegalRiskValidationException("Complete legal risk draft is required."); }
    private static void ValidateEvaluation(LegalRiskEvaluateCommand c) { if(string.IsNullOrWhiteSpace(c.Evidence)||c.Findings.Any(f=>f.FindingId==Guid.Empty||string.IsNullOrWhiteSpace(f.Citation)||string.IsNullOrWhiteSpace(f.AffectedParty)||string.IsNullOrWhiteSpace(f.Jurisdiction)||f.Confidence<0||f.Confidence>1||string.IsNullOrWhiteSpace(f.Rationale)||string.IsNullOrWhiteSpace(f.Evidence)||string.IsNullOrWhiteSpace(f.ProposedMitigation))) throw new LegalRiskValidationException("Complete findings and evidence are required."); }

    private static void RequireAuthority(SqliteConnection c,SqliteTransaction tx,LegalRiskDraft d)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT project_id,revision,status,asset_id,asset_digest,asset_version FROM ai_provenance_records WHERE workspace_id=$w AND record_id=$id";cmd.Parameters.AddWithValue("$w",d.WorkspaceId);cmd.Parameters.AddWithValue("$id",d.ProvenanceRecordId.ToString("D"));using var r=cmd.ExecuteReader();
        if(!r.Read()) throw new LegalRiskValidationException("Approved provenance authority was not found."); var status=r.GetString(2); var digest=Hash($"{d.WorkspaceId}:{d.ProvenanceRecordId:D}:{r.GetInt64(1)}:{status}"); if(Guid.Parse(r.GetString(0))!=d.ProjectId||r.GetInt64(1)!=d.ExpectedProvenanceRevision||status!="APPROVED"||digest!=d.ExpectedProvenanceDigest||Guid.Parse(r.GetString(3))!=d.SubjectId||r.GetString(4)!=d.SubjectDigest||r.GetInt32(5)!=d.SubjectVersion) throw new LegalRiskValidationException("Provenance authority is not exact and approved.");
    }
    private static bool AuthorityMatches(SqliteConnection c,SqliteTransaction tx,LegalRiskCase item) { try { RequireAuthority(c,tx,new(item.CaseId,item.ProjectId,item.WorkspaceId,item.ProvenanceRecordId,item.ExpectedProvenanceRevision,item.ExpectedProvenanceDigest,item.SubjectId,item.SubjectReference,item.SubjectDigest,item.SubjectVersion,item.Jurisdictions,item.PolicyVersion,"system","{}","authority-check")); return true; } catch(LegalRiskValidationException){return false;} }

    private sealed record StoredReceipt(Guid CaseId,string Operation,string Fingerprint,string Hash);
    private static StoredReceipt? ReadReceipt(SqliteConnection c,SqliteTransaction? tx,string w,Guid request){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT case_id,operation,request_fingerprint,payload_hash FROM legal_risk_receipts WHERE workspace_id=$w AND request_id=$r";cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$r",request.ToString("D"));using var r=cmd.ExecuteReader();return r.Read()?new(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),r.GetString(3)):null;}
    private static void RequireReceipt(StoredReceipt? r,string operation,Guid id,string fingerprint,string hash){if(r is null||r.CaseId!=id||r.Operation!=operation||r.Fingerprint!=fingerprint||r.Hash!=hash) throw new LegalRiskConflictException("Request id reused with different payload.");}
    private static LegalRiskCase Require(SqliteConnection c,SqliteTransaction? tx,string w,Guid id)=>Read(c,tx,w,id)??throw new LegalRiskValidationException("Legal risk case not found.");
    private static LegalRiskCase? Read(SqliteConnection c,SqliteTransaction? tx,string w,Guid id)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT project_id,provenance_record_id,expected_provenance_revision,expected_provenance_digest,subject_id,subject_reference,subject_digest,subject_version,jurisdictions_json,policy_version,revision,status,evidence,decision,decision_reason,message_id,created_at_utc,updated_at_utc FROM legal_risk_cases WHERE workspace_id=$w AND case_id=$id";cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$id",id.ToString("D"));using var r=cmd.ExecuteReader();if(!r.Read())return null;
        var item=new LegalRiskCase(id,Guid.Parse(r.GetString(0)),w,Guid.Parse(r.GetString(1)),r.GetInt64(2),r.GetString(3),Guid.Parse(r.GetString(4)),r.GetString(5),r.GetString(6),r.GetInt32(7),JsonSerializer.Deserialize<List<string>>(r.GetString(8))??[],r.GetString(9),r.GetInt64(10),Enum.Parse<LegalRiskStatus>(Normalize(r.GetString(11))),[],[],r.IsDBNull(12)?null:r.GetString(12),r.IsDBNull(13)?null:Enum.Parse<LegalRiskDecision>(Normalize(r.GetString(13))),r.IsDBNull(14)?null:r.GetString(14),r.IsDBNull(15)?null:Guid.Parse(r.GetString(15)),DateTimeOffset.Parse(r.GetString(16)),DateTimeOffset.Parse(r.GetString(17)));
        r.Close(); return item with { Findings=ReadFindings(c,tx,w,id),Reviews=ReadReviews(c,tx,w,id) };
    }
    private static IReadOnlyList<LegalRiskFinding> ReadFindings(SqliteConnection c,SqliteTransaction? tx,string w,Guid id){var list=new List<LegalRiskFinding>();using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT finding_id,category,citation,affected_party,jurisdiction,severity,confidence,rationale,evidence,proposed_mitigation,policy_mandates_human_review,resolved FROM legal_risk_findings WHERE workspace_id=$w AND case_id=$id ORDER BY finding_id";cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$id",id.ToString("D"));using var r=cmd.ExecuteReader();while(r.Read())list.Add(new(Guid.Parse(r.GetString(0)),Enum.Parse<LegalRiskCategory>(Normalize(r.GetString(1))),r.GetString(2),r.GetString(3),r.GetString(4),Enum.Parse<LegalRiskSeverity>(Normalize(r.GetString(5))),r.GetDecimal(6),r.GetString(7),r.GetString(8),r.GetString(9),r.GetInt32(10)==1,r.GetInt32(11)==1));return list;}
    private static IReadOnlyList<LegalRiskHumanReview> ReadReviews(SqliteConnection c,SqliteTransaction? tx,string w,Guid id){var list=new List<LegalRiskHumanReview>();using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT review_id,reviewer_identity,reviewer_role,scope,decision,rationale,evidence,conditions,expires_at_utc,reviewed_at_utc FROM legal_risk_reviews WHERE workspace_id=$w AND case_id=$id ORDER BY reviewed_at_utc";cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$id",id.ToString("D"));using var r=cmd.ExecuteReader();while(r.Read())list.Add(new(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),r.GetString(3),Enum.Parse<LegalHumanDecision>(Normalize(r.GetString(4))),r.GetString(5),r.GetString(6),r.IsDBNull(7)?null:r.GetString(7),r.IsDBNull(8)?null:DateTimeOffset.Parse(r.GetString(8)),DateTimeOffset.Parse(r.GetString(9))));return list;}

    private static string Normalize(string value)=>string.Concat(value.Split('_',StringSplitOptions.RemoveEmptyEntries).Select(x=>char.ToUpperInvariant(x[0])+x[1..].ToLowerInvariant()));
    private static void History(SqliteConnection c,SqliteTransaction tx,string w,Guid id,long rev,string transition,string actor,string? reason,object payload,DateTimeOffset at)=>Execute(c,tx,"INSERT INTO legal_risk_history(workspace_id,case_id,revision,transition,actor,reason,payload_json,occurred_at_utc) VALUES($w,$id,$r,$t,$a,$reason,$p,$at)",("$w",w),("$id",id.ToString("D")),("$r",rev),("$t",transition),("$a",actor),("$reason",(object?)reason??DBNull.Value),("$p",JsonSerializer.Serialize(payload)),("$at",Text(at)));
    private static void Receipt(SqliteConnection c,SqliteTransaction tx,string w,Guid request,Guid id,string operation,string fingerprint,string hash,long revision,Guid? message,DateTimeOffset at)=>Execute(c,tx,"INSERT INTO legal_risk_receipts(workspace_id,request_id,case_id,operation,request_fingerprint,payload_hash,result_revision,message_id,created_at_utc) VALUES($w,$r,$id,$o,$f,$h,$rev,$m,$at)",("$w",w),("$r",request.ToString("D")),("$id",id.ToString("D")),("$o",operation),("$f",fingerprint),("$h",hash),("$rev",revision),("$m",message is null?DBNull.Value:message.Value.ToString("D")),("$at",Text(at)));
    private static void Outbox(SqliteConnection c,SqliteTransaction tx,Guid id,string type,object payload,DateTimeOffset at)=>Execute(c,tx,"INSERT INTO outbox_messages(message_id,event_type,payload_json,occurred_at_utc,status,attempt_count,next_attempt_at_utc) VALUES($id,$type,$payload,$at,'Pending',0,$at)",("$id",id.ToString("D")),("$type",type),("$payload",JsonSerializer.Serialize(payload)),("$at",Text(at)));
    private static void Execute(SqliteConnection c,SqliteTransaction tx,string sql,params (string Name,object Value)[] values){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var v in values)cmd.Parameters.AddWithValue(v.Name,v.Value);cmd.ExecuteNonQuery();}
    private static Guid MessageId(Guid source){var b=SHA256.HashData(Encoding.UTF8.GetBytes("legal-risk:"+source.ToString("D")));return new Guid(b[..16]);}
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Text(DateTimeOffset value)=>value.ToUniversalTime().ToString("O");
}

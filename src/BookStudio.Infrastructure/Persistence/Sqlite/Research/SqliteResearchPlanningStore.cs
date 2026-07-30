using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Research;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Research;

public sealed class SqliteResearchPlanningStore : IResearchPlanningStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteResearchPlanningStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<ResearchPlanCreateResult> CreateAsync(ResearchPlanDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(d);
        var hash = Hash(JsonSerializer.Serialize(new { d.ProjectId, d.WorkspaceId, d.OriginalityReviewId, d.ExpectedOriginalityRevision, d.ExpectedOriginalityDigest, d.Version, d.Actor, d.Evidence, d.Questions }));
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, d.WorkspaceId, d.PlanId);
            if (existing is not null)
            {
                var receipt = ReadReceipt(c, tx, d.WorkspaceId, d.PlanId) ?? throw new ResearchPlanningConflictException("Create receipt missing.");
                RequireReceipt(receipt, "CREATE", d.WorkspaceId, d.PlanId, d.RequestFingerprint, hash);
                return new ResearchPlanCreateResult(existing, true);
            }
            RequireAuthority(c, tx, d.WorkspaceId, d.ProjectId, d.OriginalityReviewId, d.ExpectedOriginalityRevision, d.ExpectedOriginalityDigest);
            ValidateQuestions(d.Questions);
            var message = MessageId(d.PlanId);
            Execute(c, tx, "INSERT INTO research_plans(workspace_id,plan_id,project_id,originality_review_id,expected_originality_revision,expected_originality_digest,version,actor,evidence,revision,status,decision,decision_reason,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$authority,$r,$digest,$v,$actor,$e,1,'PROPOSED',NULL,NULL,$m,$at,$at);", ("$w", d.WorkspaceId), ("$id", d.PlanId.ToString("D")), ("$p", d.ProjectId.ToString("D")), ("$authority", d.OriginalityReviewId.ToString("D")), ("$r", d.ExpectedOriginalityRevision), ("$digest", d.ExpectedOriginalityDigest), ("$v", d.Version), ("$actor", d.Actor), ("$e", d.Evidence), ("$m", message.ToString("D")), ("$at", Text(at)));
            ReplaceQuestions(c, tx, d.WorkspaceId, d.PlanId, d.Questions);
            InsertHistory(c, tx, d.WorkspaceId, d.PlanId, 1, "CREATE", d.Actor, null, new { QuestionCount = d.Questions.Count }, at);
            InsertReceipt(c, tx, d.WorkspaceId, d.PlanId, d.PlanId, "CREATE", d.RequestFingerprint, hash, 1, message, at);
            InsertOutbox(c, tx, message, "research.plan.proposed", new { d.WorkspaceId, d.PlanId, d.ProjectId, d.Actor }, at);
            return new ResearchPlanCreateResult(Require(c, tx, d.WorkspaceId, d.PlanId), false);
        }, ct);
    }

    public ValueTask<ResearchPlan> UpdateAsync(ResearchPlanUpdateCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateQuestions(cmd.Questions);
        if (string.IsNullOrWhiteSpace(cmd.Evidence)) throw new ResearchPlanningValidationException("Evidence is required.");
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.PlanId, "UPDATE", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(cmd)), cmd.ExpectedRevision, at, (c, tx, plan) =>
        {
            if (!AuthorityMatches(c, tx, plan)) return MarkStaleCore(c, tx, plan, cmd.RequestId, "Originality authority drift.", cmd.Actor, at);
            if (plan.Status is ResearchPlanStatus.Approved or ResearchPlanStatus.Stale) throw new ResearchPlanningTransitionException("Plan cannot be updated from its current state.");
            ReplaceQuestions(c, tx, plan.WorkspaceId, plan.PlanId, cmd.Questions);
            var status = cmd.Questions.All(q => q.Status is ResearchQuestionStatus.Ready or ResearchQuestionStatus.Completed) ? ResearchPlanStatus.Ready : ResearchPlanStatus.Proposed;
            return Advance(c, tx, plan, status, cmd.RequestId, "UPDATE", cmd.Actor, null, new { cmd.Evidence, QuestionCount = cmd.Questions.Count }, "research.plan.updated", at, evidence: cmd.Evidence);
        }, ct);
    }

    public ValueTask<ResearchPlan> DecideAsync(ResearchPlanDecisionCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new ResearchPlanningValidationException("Decision reason is required.");
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.PlanId, "DECIDE", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(cmd)), cmd.ExpectedRevision, at, (c, tx, plan) =>
        {
            if (!AuthorityMatches(c, tx, plan)) return MarkStaleCore(c, tx, plan, cmd.RequestId, "Originality authority drift.", cmd.Actor, at);
            if (cmd.Decision == ResearchPlanDecision.Approve)
            {
                if (plan.Status != ResearchPlanStatus.Ready) throw new ResearchPlanningTransitionException("Only ready plans can be approved.");
                if (plan.Questions.Count == 0 || plan.Questions.Any(q => q.Status is ResearchQuestionStatus.Blocked or ResearchQuestionStatus.Rejected || string.IsNullOrWhiteSpace(q.ExpectedEvidence))) throw new ResearchPlanningTransitionException("Incomplete or blocked questions prevent approval.");
            }
            var status = cmd.Decision == ResearchPlanDecision.Approve ? ResearchPlanStatus.Approved : ResearchPlanStatus.Blocked;
            var revision = plan.Revision + 1; var message = MessageId(cmd.RequestId);
            Execute(c, tx, "UPDATE research_plans SET revision=$r,status=$s,decision=$d,decision_reason=$reason,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND plan_id=$id;", ("$r", revision), ("$s", status.ToString().ToUpperInvariant()), ("$d", cmd.Decision.ToString().ToUpperInvariant()), ("$reason", cmd.Reason), ("$m", message.ToString("D")), ("$at", Text(at)), ("$w", plan.WorkspaceId), ("$id", plan.PlanId.ToString("D")));
            InsertHistory(c, tx, plan.WorkspaceId, plan.PlanId, revision, "DECIDE", cmd.Actor, cmd.Reason, new { cmd.Decision }, at);
            InsertOutbox(c, tx, message, cmd.Decision == ResearchPlanDecision.Approve ? "research.plan.approved" : "research.plan.blocked", new { plan.WorkspaceId, plan.PlanId, cmd.Decision, cmd.Actor }, at);
            return Require(c, tx, plan.WorkspaceId, plan.PlanId);
        }, ct);
    }

    public ValueTask<ResearchPlan> MarkStaleAsync(ResearchPlanStaleCommand cmd, DateTimeOffset at, CancellationToken ct = default) => Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.PlanId, "STALE", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(cmd)), cmd.ExpectedRevision, at, (c, tx, plan) => MarkStaleCore(c, tx, plan, cmd.RequestId, cmd.Reason, cmd.Actor, at), ct);
    public async ValueTask<ResearchPlan?> GetAsync(string workspaceId, Guid planId, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); using var c = _factory.OpenConnection(); return await Task.FromResult(Read(c, null, workspaceId, planId)); }
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false); }

    private ValueTask<ResearchPlan> Mutate(Guid requestId, string workspace, Guid planId, string action, string fingerprint, string hash, long expectedRevision, DateTimeOffset at, Func<SqliteConnection, SqliteTransaction, ResearchPlan, ResearchPlan> mutation, CancellationToken ct) => _queue.ExecuteInTransactionAsync((c, tx, token) => { token.ThrowIfCancellationRequested(); var receipt = ReadReceipt(c, tx, workspace, requestId); if (receipt is not null) { RequireReceipt(receipt, action, workspace, planId, fingerprint, hash); return Require(c, tx, workspace, planId); } var plan = Require(c, tx, workspace, planId); if (plan.Revision != expectedRevision) throw new ResearchPlanningConflictException("Stale revision."); var result = mutation(c, tx, plan); InsertReceipt(c, tx, workspace, requestId, planId, action, fingerprint, hash, result.Revision, result.MessageId, at); return result; }, ct);
    private static ResearchPlan Advance(SqliteConnection c, SqliteTransaction tx, ResearchPlan p, ResearchPlanStatus status, Guid request, string action, string actor, string? reason, object payload, string eventType, DateTimeOffset at, string? evidence = null) { var revision = p.Revision + 1; var message = MessageId(request); Execute(c, tx, "UPDATE research_plans SET revision=$r,status=$s,evidence=$e,decision=NULL,decision_reason=$reason,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND plan_id=$id;", ("$r", revision), ("$s", status.ToString().ToUpperInvariant()), ("$e", evidence ?? p.Evidence), ("$reason", reason is null ? DBNull.Value : reason), ("$m", message.ToString("D")), ("$at", Text(at)), ("$w", p.WorkspaceId), ("$id", p.PlanId.ToString("D"))); InsertHistory(c, tx, p.WorkspaceId, p.PlanId, revision, action, actor, reason, payload, at); InsertOutbox(c, tx, message, eventType, new { p.WorkspaceId, p.PlanId, Actor = actor }, at); return Require(c, tx, p.WorkspaceId, p.PlanId); }
    private static ResearchPlan MarkStaleCore(SqliteConnection c, SqliteTransaction tx, ResearchPlan p, Guid request, string reason, string actor, DateTimeOffset at) { if (string.IsNullOrWhiteSpace(reason)) throw new ResearchPlanningValidationException("Stale reason is required."); if (p.Status == ResearchPlanStatus.Approved) throw new ResearchPlanningTransitionException("Approved plan cannot be marked stale."); return Advance(c, tx, p, ResearchPlanStatus.Stale, request, "STALE", actor, reason, new { reason }, "research.plan.stale", at); }

    private static void ValidateDraft(ResearchPlanDraft d) { if (d.PlanId == Guid.Empty || d.ProjectId == Guid.Empty || d.OriginalityReviewId == Guid.Empty || d.ExpectedOriginalityRevision < 1 || d.Version < 1 || string.IsNullOrWhiteSpace(d.WorkspaceId) || string.IsNullOrWhiteSpace(d.ExpectedOriginalityDigest) || string.IsNullOrWhiteSpace(d.Actor) || string.IsNullOrWhiteSpace(d.Evidence) || string.IsNullOrWhiteSpace(d.RequestFingerprint)) throw new ResearchPlanningValidationException("Complete research plan is required."); ValidateQuestions(d.Questions); }
    private static void ValidateQuestions(IReadOnlyList<ResearchQuestionDraft> questions) { if (questions is null || questions.Count == 0) throw new ResearchPlanningValidationException("At least one research question is required."); if (questions.Select(q => q.QuestionId).Distinct().Count() != questions.Count) throw new ResearchPlanningValidationException("Question ids must be unique."); var ids = questions.Select(q => q.QuestionId).ToHashSet(); foreach (var q in questions) { if (q.QuestionId == Guid.Empty || string.IsNullOrWhiteSpace(q.Location) || string.IsNullOrWhiteSpace(q.Question) || string.IsNullOrWhiteSpace(q.SourceStrategy) || string.IsNullOrWhiteSpace(q.QualityCriteria) || string.IsNullOrWhiteSpace(q.CurrencyCriteria) || string.IsNullOrWhiteSpace(q.CoverageCriteria) || string.IsNullOrWhiteSpace(q.ExpectedEvidence) || q.Attempts < 0) throw new ResearchPlanningValidationException("Complete question evidence is required."); if (q.DependencyQuestionIds.Any(id => id == q.QuestionId || !ids.Contains(id))) throw new ResearchPlanningValidationException("Question dependency is invalid."); } }

    private static void RequireAuthority(SqliteConnection c, SqliteTransaction tx, string workspace, Guid project, Guid authorityId, long expectedRevision, string expectedDigest) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT project_id,revision,status FROM originality_read_aloud_reviews WHERE workspace_id=$w AND review_id=$id;"; cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$id", authorityId.ToString("D")); using var reader = cmd.ExecuteReader(); if (!reader.Read()) throw new ResearchPlanningValidationException("Approved originality authority was not found."); var storedProject = Guid.Parse(reader.GetString(0)); var revision = reader.GetInt64(1); var status = reader.GetString(2); var digest = Hash($"{workspace}:{authorityId:D}:{revision}:{status}"); if (storedProject != project || revision != expectedRevision || status != "APPROVED" || digest != expectedDigest) throw new ResearchPlanningValidationException("Originality authority is not exact and approved."); }
    private static bool AuthorityMatches(SqliteConnection c, SqliteTransaction tx, ResearchPlan p) { try { RequireAuthority(c, tx, p.WorkspaceId, p.ProjectId, p.OriginalityReviewId, p.ExpectedOriginalityRevision, p.ExpectedOriginalityDigest); return true; } catch (ResearchPlanningValidationException) { return false; } }

    private sealed record Receipt(string Workspace, Guid PlanId, string Action, string Fingerprint, string Hash);
    private static Receipt? ReadReceipt(SqliteConnection c, SqliteTransaction? tx, string w, Guid request) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT workspace_id,plan_id,action,request_fingerprint,payload_hash FROM research_plan_receipts WHERE workspace_id=$w AND request_id=$r;"; cmd.Parameters.AddWithValue("$w", w); cmd.Parameters.AddWithValue("$r", request.ToString("D")); using var x = cmd.ExecuteReader(); return x.Read() ? new(x.GetString(0), Guid.Parse(x.GetString(1)), x.GetString(2), x.GetString(3), x.GetString(4)) : null; }
    private static void RequireReceipt(Receipt r, string action, string workspace, Guid plan, string fingerprint, string hash) { if (r.Workspace != workspace || r.PlanId != plan || r.Action != action || r.Fingerprint != fingerprint || r.Hash != hash) throw new ResearchPlanningConflictException("Request id reused with different payload."); }
    private static ResearchPlan Require(SqliteConnection c, SqliteTransaction? tx, string w, Guid id) => Read(c, tx, w, id) ?? throw new ResearchPlanningValidationException("Research plan not found.");
    private static ResearchPlan? Read(SqliteConnection c, SqliteTransaction? tx, string w, Guid id) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT project_id,originality_review_id,expected_originality_revision,expected_originality_digest,version,actor,evidence,revision,status,decision,decision_reason,message_id,created_at_utc,updated_at_utc FROM research_plans WHERE workspace_id=$w AND plan_id=$id;"; cmd.Parameters.AddWithValue("$w", w); cmd.Parameters.AddWithValue("$id", id.ToString("D")); using var r = cmd.ExecuteReader(); if (!r.Read()) return null; var plan = new { Project = Guid.Parse(r.GetString(0)), Authority = Guid.Parse(r.GetString(1)), AuthorityRevision = r.GetInt64(2), Digest = r.GetString(3), Version = r.GetInt32(4), Actor = r.GetString(5), Evidence = r.GetString(6), Revision = r.GetInt64(7), Status = Enum.Parse<ResearchPlanStatus>(r.GetString(8), true), Decision = r.IsDBNull(9) ? (ResearchPlanDecision?)null : Enum.Parse<ResearchPlanDecision>(r.GetString(9), true), Reason = r.IsDBNull(10) ? null : r.GetString(10), Message = r.IsDBNull(11) ? (Guid?)null : Guid.Parse(r.GetString(11)), Created = DateTimeOffset.Parse(r.GetString(12), CultureInfo.InvariantCulture), Updated = DateTimeOffset.Parse(r.GetString(13), CultureInfo.InvariantCulture) }; r.Close(); var questions = new List<ResearchQuestion>(); using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = "SELECT question_id,type,priority,location,claim_ids_json,editorial_decision_ids_json,question,source_strategy,quality_criteria,currency_criteria,coverage_criteria,expected_evidence,dependency_question_ids_json,owner,status,attempts FROM research_questions WHERE workspace_id=$w AND plan_id=$id ORDER BY question_id;"; q.Parameters.AddWithValue("$w", w); q.Parameters.AddWithValue("$id", id.ToString("D")); using var qr = q.ExecuteReader(); while (qr.Read()) questions.Add(new(Guid.Parse(qr.GetString(0)), Enum.Parse<ResearchQuestionType>(qr.GetString(1), true), Enum.Parse<ResearchPriority>(qr.GetString(2), true), qr.GetString(3), JsonSerializer.Deserialize<List<string>>(qr.GetString(4)) ?? [], JsonSerializer.Deserialize<List<string>>(qr.GetString(5)) ?? [], qr.GetString(6), qr.GetString(7), qr.GetString(8), qr.GetString(9), qr.GetString(10), qr.GetString(11), JsonSerializer.Deserialize<List<Guid>>(qr.GetString(12)) ?? [], qr.IsDBNull(13) ? null : qr.GetString(13), Enum.Parse<ResearchQuestionStatus>(qr.GetString(14), true), qr.GetInt32(15))); return new(id, plan.Project, w, plan.Authority, plan.AuthorityRevision, plan.Digest, plan.Version, plan.Actor, plan.Evidence, plan.Revision, plan.Status, questions, plan.Decision, plan.Reason, plan.Message, plan.Created, plan.Updated); }

    private static void ReplaceQuestions(SqliteConnection c, SqliteTransaction tx, string w, Guid id, IReadOnlyList<ResearchQuestionDraft> questions) { Execute(c, tx, "DELETE FROM research_questions WHERE workspace_id=$w AND plan_id=$id;", ("$w", w), ("$id", id.ToString("D"))); foreach (var q in questions) Execute(c, tx, "INSERT INTO research_questions(workspace_id,plan_id,question_id,type,priority,location,claim_ids_json,editorial_decision_ids_json,question,source_strategy,quality_criteria,currency_criteria,coverage_criteria,expected_evidence,dependency_question_ids_json,owner,status,attempts) VALUES($w,$id,$qid,$t,$p,$l,$claims,$decisions,$q,$source,$quality,$currency,$coverage,$expected,$deps,$owner,$status,$attempts);", ("$w", w), ("$id", id.ToString("D")), ("$qid", q.QuestionId.ToString("D")), ("$t", q.Type.ToString().ToUpperInvariant()), ("$p", q.Priority.ToString().ToUpperInvariant()), ("$l", q.Location), ("$claims", JsonSerializer.Serialize(q.ClaimIds)), ("$decisions", JsonSerializer.Serialize(q.EditorialDecisionIds)), ("$q", q.Question), ("$source", q.SourceStrategy), ("$quality", q.QualityCriteria), ("$currency", q.CurrencyCriteria), ("$coverage", q.CoverageCriteria), ("$expected", q.ExpectedEvidence), ("$deps", JsonSerializer.Serialize(q.DependencyQuestionIds)), ("$owner", q.Owner is null ? DBNull.Value : q.Owner), ("$status", q.Status.ToString().ToUpperInvariant()), ("$attempts", q.Attempts)); }
    private static void InsertHistory(SqliteConnection c, SqliteTransaction tx, string w, Guid id, long rev, string action, string actor, string? reason, object payload, DateTimeOffset at) => Execute(c, tx, "INSERT INTO research_plan_history(workspace_id,plan_id,revision,action,actor,reason,payload_json,occurred_at_utc) VALUES($w,$id,$r,$a,$actor,$reason,$p,$at);", ("$w", w), ("$id", id.ToString("D")), ("$r", rev), ("$a", action), ("$actor", actor), ("$reason", reason is null ? DBNull.Value : reason), ("$p", JsonSerializer.Serialize(payload)), ("$at", Text(at)));
    private static void InsertReceipt(SqliteConnection c, SqliteTransaction tx, string w, Guid request, Guid id, string action, string fingerprint, string hash, long rev, Guid? message, DateTimeOffset at) => Execute(c, tx, "INSERT INTO research_plan_receipts(workspace_id,request_id,plan_id,action,request_fingerprint,payload_hash,resulting_revision,message_id,created_at_utc) VALUES($w,$r,$id,$a,$f,$h,$v,$m,$at);", ("$w", w), ("$r", request.ToString("D")), ("$id", id.ToString("D")), ("$a", action), ("$f", fingerprint), ("$h", hash), ("$v", rev), ("$m", message is null ? DBNull.Value : message.Value.ToString("D")), ("$at", Text(at)));
    private static void InsertOutbox(SqliteConnection c, SqliteTransaction tx, Guid message, string type, object payload, DateTimeOffset at) => Execute(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($id,$t,'1.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);", ("$id", message.ToString("D")), ("$t", type), ("$p", JsonSerializer.Serialize(payload)), ("$at", Text(at)));
    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] values) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var (n, v) in values) cmd.Parameters.AddWithValue(n, v); cmd.ExecuteNonQuery(); }
    private static Guid MessageId(Guid request) => new(SHA256.HashData(request.ToByteArray()).AsSpan(0, 16));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Text(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
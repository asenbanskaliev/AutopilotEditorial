using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteCopyeditProofreadingStore : ICopyeditProofreadingStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteCopyeditProofreadingStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<CopyeditProofreadingReviewCreateResult> CreateAsync(CopyeditProofreadingReviewDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(d);
        var hash = Hash(JsonSerializer.Serialize(new { d.ProjectId, d.WorkspaceId, d.EditorialPlanId, d.ThemesPacingReviewId, d.ExpectedThemesPacingRevision, d.ExpectedThemesPacingDigest, d.Version, d.RuleSet, d.StyleGuide, d.LanguageTag, d.Actor, d.SnapshotJson }));
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, d.WorkspaceId, d.ReviewId);
            if (existing is not null)
            {
                var receipt = ReadReceipt(c, tx, d.WorkspaceId, d.ReviewId) ?? throw new CopyeditProofreadingConflictException("Create receipt missing.");
                RequireReceipt(receipt, "CREATE", d.WorkspaceId, d.ReviewId, d.RequestFingerprint, hash);
                return new CopyeditProofreadingReviewCreateResult(existing, true);
            }
            RequireAuthority(c, tx, d.WorkspaceId, d.ProjectId, d.EditorialPlanId, d.ThemesPacingReviewId, d.ExpectedThemesPacingRevision, d.ExpectedThemesPacingDigest);
            var message = MessageId(d.ReviewId);
            Execute(c, tx, "INSERT INTO copyedit_proofreading_reviews(workspace_id,review_id,project_id,editorial_plan_id,themes_pacing_review_id,expected_themes_pacing_revision,expected_themes_pacing_digest,version,rule_set,style_guide,language_tag,actor,snapshot_json,revision,status,decision,decision_reason,expected_repair_revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$plan,$authority,$r,$digest,$v,$rules,$style,$lang,$actor,$snapshot,1,'PROPOSED',NULL,NULL,NULL,$m,$at,$at);",
                ("$w", d.WorkspaceId), ("$id", d.ReviewId.ToString("D")), ("$p", d.ProjectId.ToString("D")), ("$plan", d.EditorialPlanId.ToString("D")), ("$authority", d.ThemesPacingReviewId.ToString("D")), ("$r", d.ExpectedThemesPacingRevision), ("$digest", d.ExpectedThemesPacingDigest), ("$v", d.Version), ("$rules", d.RuleSet), ("$style", d.StyleGuide), ("$lang", d.LanguageTag), ("$actor", d.Actor), ("$snapshot", d.SnapshotJson), ("$m", message.ToString("D")), ("$at", Text(at)));
            InsertHistory(c, tx, d.WorkspaceId, d.ReviewId, 1, "CREATE", d.Actor, null, new { d.ProjectId, d.EditorialPlanId, d.ThemesPacingReviewId }, at);
            InsertReceipt(c, tx, d.WorkspaceId, d.ReviewId, d.ReviewId, "CREATE", d.RequestFingerprint, hash, 1, message, at);
            InsertOutbox(c, tx, message, "editorial.copyedit-proofreading.proposed", new { d.WorkspaceId, d.ReviewId, d.ProjectId, d.Actor }, at);
            return new CopyeditProofreadingReviewCreateResult(Require(c, tx, d.WorkspaceId, d.ReviewId), false);
        }, ct);
    }

    public ValueTask<CopyeditProofreadingReview> EvaluateAsync(CopyeditProofreadingEvaluateCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (cmd.Findings is null || string.IsNullOrWhiteSpace(cmd.Evidence)) throw new CopyeditProofreadingValidationException("Findings and evidence are required.");
        ValidateFindings(cmd.Findings);
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.ReviewId, "EVALUATE", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.ReviewId, cmd.ExpectedRevision, cmd.Findings, cmd.Evidence, cmd.Actor })), cmd.ExpectedRevision, at, (c, tx, review) =>
        {
            if (!AuthorityMatches(c, tx, review)) return MarkStaleCore(c, tx, review, cmd.RequestId, "Themes/pacing authority drift.", cmd.Actor, at);
            if (review.Status is not (CopyeditProofreadingReviewStatus.Proposed or CopyeditProofreadingReviewStatus.RepairRequired)) throw new CopyeditProofreadingTransitionException("Review cannot be evaluated from its current state.");
            Execute(c, tx, "DELETE FROM copyedit_proofreading_findings WHERE workspace_id=$w AND review_id=$id;", ("$w", review.WorkspaceId), ("$id", review.ReviewId.ToString("D")));
            foreach (var f in cmd.Findings)
                Execute(c, tx, "INSERT INTO copyedit_proofreading_findings(workspace_id,review_id,finding_id,area,severity,rule,location,chapter_numbers_json,scene_ids_json,paragraph_ids_json,spans_json,suggested_correction,evidence,is_open) VALUES($w,$id,$fid,$a,$s,$r,$l,$c,$sc,$p,$sp,$fix,$e,$o);",
                    ("$w", review.WorkspaceId), ("$id", review.ReviewId.ToString("D")), ("$fid", f.FindingId.ToString("D")), ("$a", f.Area.ToString().ToUpperInvariant()), ("$s", f.Severity.ToString().ToUpperInvariant()), ("$r", f.Rule), ("$l", f.Location), ("$c", JsonSerializer.Serialize(f.ChapterNumbers)), ("$sc", JsonSerializer.Serialize(f.SceneIds)), ("$p", JsonSerializer.Serialize(f.ParagraphIds)), ("$sp", JsonSerializer.Serialize(f.Spans)), ("$fix", f.SuggestedCorrection), ("$e", f.Evidence), ("$o", f.IsOpen ? 1 : 0));
            return Advance(c, tx, review, CopyeditProofreadingReviewStatus.Evaluated, cmd.RequestId, "EVALUATE", cmd.Actor, null, new { cmd.Evidence, FindingCount = cmd.Findings.Count }, "editorial.copyedit-proofreading.evaluated", at);
        }, ct);
    }

    public ValueTask<CopyeditProofreadingReview> DecideAsync(CopyeditProofreadingDecisionCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new CopyeditProofreadingValidationException("Decision reason is required.");
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.ReviewId, "DECIDE", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(cmd)), cmd.ExpectedRevision, at, (c, tx, review) =>
        {
            if (!AuthorityMatches(c, tx, review)) return MarkStaleCore(c, tx, review, cmd.RequestId, "Themes/pacing authority drift.", cmd.Actor, at);
            if (review.Status != CopyeditProofreadingReviewStatus.Evaluated) throw new CopyeditProofreadingTransitionException("Only evaluated reviews can be decided.");
            if (cmd.Decision == CopyeditProofreadingDecision.Approve && review.Findings.Any(f => f.IsOpen && f.Severity == CopyeditProofreadingSeverity.Blocking)) throw new CopyeditProofreadingTransitionException("Blocking findings prevent approval.");
            if (cmd.Decision == CopyeditProofreadingDecision.ReturnToRepair && cmd.ExpectedRepairRevision is null) throw new CopyeditProofreadingValidationException("Repair revision is required.");
            var status = cmd.Decision switch { CopyeditProofreadingDecision.Approve => CopyeditProofreadingReviewStatus.Approved, CopyeditProofreadingDecision.Reject => CopyeditProofreadingReviewStatus.Rejected, _ => CopyeditProofreadingReviewStatus.RepairRequired };
            var revision = review.Revision + 1; var message = MessageId(cmd.RequestId);
            UpdateReview(c, tx, review, status, revision, cmd.Decision, cmd.Reason, cmd.ExpectedRepairRevision, message, at);
            InsertHistory(c, tx, review.WorkspaceId, review.ReviewId, revision, "DECIDE", cmd.Actor, cmd.Reason, new { cmd.Decision, cmd.ExpectedRepairRevision }, at);
            InsertOutbox(c, tx, message, cmd.Decision switch { CopyeditProofreadingDecision.Approve => "editorial.copyedit-proofreading.approved", CopyeditProofreadingDecision.Reject => "editorial.copyedit-proofreading.rejected", _ => "editorial.copyedit-proofreading.repair-required" }, new { review.WorkspaceId, review.ReviewId, cmd.Decision, cmd.Actor }, at);
            return Require(c, tx, review.WorkspaceId, review.ReviewId);
        }, ct);
    }

    public ValueTask<CopyeditProofreadingReview> ReopenAsync(CopyeditProofreadingReopenCommand cmd, DateTimeOffset at, CancellationToken ct = default) => Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.ReviewId, "REOPEN", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(cmd)), cmd.ExpectedRevision, at, (c, tx, review) =>
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new CopyeditProofreadingValidationException("Reopen reason is required.");
        if (review.Status is not (CopyeditProofreadingReviewStatus.Approved or CopyeditProofreadingReviewStatus.Rejected or CopyeditProofreadingReviewStatus.Stale)) throw new CopyeditProofreadingTransitionException("Review cannot be reopened.");
        return Advance(c, tx, review, CopyeditProofreadingReviewStatus.Proposed, cmd.RequestId, "REOPEN", cmd.Actor, cmd.Reason, new { cmd.Reason }, "editorial.copyedit-proofreading.reopened", at);
    }, ct);

    public ValueTask<CopyeditProofreadingReview> MarkStaleAsync(CopyeditProofreadingStaleCommand cmd, DateTimeOffset at, CancellationToken ct = default) => Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.ReviewId, "STALE", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(cmd)), cmd.ExpectedRevision, at, (c, tx, review) => MarkStaleCore(c, tx, review, cmd.RequestId, cmd.Reason, cmd.Actor, at), ct);

    public async ValueTask<CopyeditProofreadingReview?> GetAsync(string workspaceId, Guid reviewId, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); using var c = _factory.OpenConnection(); return await Task.FromResult(Read(c, null, workspaceId, reviewId)); }
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false); }

    private ValueTask<CopyeditProofreadingReview> Mutate(Guid requestId, string workspace, Guid reviewId, string action, string fingerprint, string hash, long expectedRevision, DateTimeOffset at, Func<SqliteConnection, SqliteTransaction, CopyeditProofreadingReview, CopyeditProofreadingReview> mutation, CancellationToken ct) => _queue.ExecuteInTransactionAsync((c, tx, token) =>
    {
        token.ThrowIfCancellationRequested(); var receipt = ReadReceipt(c, tx, workspace, requestId);
        if (receipt is not null) { RequireReceipt(receipt, action, workspace, reviewId, fingerprint, hash); return Require(c, tx, workspace, reviewId); }
        var review = Require(c, tx, workspace, reviewId); if (review.Revision != expectedRevision) throw new CopyeditProofreadingConflictException("Stale revision.");
        var result = mutation(c, tx, review); InsertReceipt(c, tx, workspace, requestId, reviewId, action, fingerprint, hash, result.Revision, result.MessageId, at); return result;
    }, ct);

    private static CopyeditProofreadingReview Advance(SqliteConnection c, SqliteTransaction tx, CopyeditProofreadingReview r, CopyeditProofreadingReviewStatus status, Guid request, string action, string actor, string? reason, object payload, string eventType, DateTimeOffset at)
    { var revision = r.Revision + 1; var message = MessageId(request); UpdateReview(c, tx, r, status, revision, null, reason, null, message, at); InsertHistory(c, tx, r.WorkspaceId, r.ReviewId, revision, action, actor, reason, payload, at); InsertOutbox(c, tx, message, eventType, new { r.WorkspaceId, r.ReviewId, Actor = actor }, at); return Require(c, tx, r.WorkspaceId, r.ReviewId); }

    private static CopyeditProofreadingReview MarkStaleCore(SqliteConnection c, SqliteTransaction tx, CopyeditProofreadingReview r, Guid request, string reason, string actor, DateTimeOffset at)
    { if (string.IsNullOrWhiteSpace(reason)) throw new CopyeditProofreadingValidationException("Stale reason is required."); if (r.Status == CopyeditProofreadingReviewStatus.Approved) throw new CopyeditProofreadingTransitionException("Approved review must be reopened first."); return Advance(c, tx, r, CopyeditProofreadingReviewStatus.Stale, request, "STALE", actor, reason, new { reason }, "editorial.copyedit-proofreading.stale", at); }

    private static void ValidateDraft(CopyeditProofreadingReviewDraft d) { if (d.ReviewId == Guid.Empty || d.ProjectId == Guid.Empty || d.EditorialPlanId == Guid.Empty || d.ThemesPacingReviewId == Guid.Empty || d.ExpectedThemesPacingRevision < 1 || d.Version < 1 || string.IsNullOrWhiteSpace(d.WorkspaceId) || string.IsNullOrWhiteSpace(d.ExpectedThemesPacingDigest) || string.IsNullOrWhiteSpace(d.RuleSet) || string.IsNullOrWhiteSpace(d.StyleGuide) || string.IsNullOrWhiteSpace(d.LanguageTag) || string.IsNullOrWhiteSpace(d.Actor) || string.IsNullOrWhiteSpace(d.SnapshotJson) || string.IsNullOrWhiteSpace(d.RequestFingerprint)) throw new CopyeditProofreadingValidationException("Complete review is required."); }
    private static void ValidateFindings(IReadOnlyList<CopyeditProofreadingFindingDraft> fs) { if (fs.Select(x => x.FindingId).Distinct().Count() != fs.Count) throw new CopyeditProofreadingValidationException("Finding ids must be unique."); foreach (var f in fs) if (f.FindingId == Guid.Empty || string.IsNullOrWhiteSpace(f.Rule) || string.IsNullOrWhiteSpace(f.Location) || string.IsNullOrWhiteSpace(f.SuggestedCorrection) || string.IsNullOrWhiteSpace(f.Evidence) || f.ChapterNumbers.Any(x => x < 1)) throw new CopyeditProofreadingValidationException("Complete finding evidence is required."); }

    private static void RequireAuthority(SqliteConnection c, SqliteTransaction tx, string workspace, Guid project, Guid plan, Guid authorityId, long expectedRevision, string expectedDigest)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT project_id,editorial_plan_id,revision,status FROM themes_pacing_reviews WHERE workspace_id=$w AND review_id=$id;"; cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$id", authorityId.ToString("D")); using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new CopyeditProofreadingValidationException("Approved themes/pacing authority was not found."); var storedProject = Guid.Parse(reader.GetString(0)); var storedPlan = Guid.Parse(reader.GetString(1)); var revision = reader.GetInt64(2); var status = reader.GetString(3); var digest = Hash($"{workspace}:{authorityId:D}:{revision}:{status}");
        if (storedProject != project || storedPlan != plan || revision != expectedRevision || status != "APPROVED" || digest != expectedDigest) throw new CopyeditProofreadingValidationException("Themes/pacing authority is not exact and approved.");
        reader.Close(); using var node = c.CreateCommand(); node.Transaction = tx; node.CommandText = "SELECT 1 FROM editorial_pass_nodes WHERE workspace_id=$w AND plan_id=$p AND pass_kind='COPYEDITPROOFREADING' AND status IN ('READY','IN_PROGRESS');"; node.Parameters.AddWithValue("$w", workspace); node.Parameters.AddWithValue("$p", plan.ToString("D")); if (node.ExecuteScalar() is null) throw new CopyeditProofreadingValidationException("Copyedit/proofreading pass is not dependency-ready.");
    }
    private static bool AuthorityMatches(SqliteConnection c, SqliteTransaction tx, CopyeditProofreadingReview r) { try { RequireAuthority(c, tx, r.WorkspaceId, r.ProjectId, r.EditorialPlanId, r.ThemesPacingReviewId, r.ExpectedThemesPacingRevision, r.ExpectedThemesPacingDigest); return true; } catch (CopyeditProofreadingValidationException) { return false; } }

    private sealed record Receipt(string Workspace, Guid ReviewId, string Action, string Fingerprint, string Hash);
    private static Receipt? ReadReceipt(SqliteConnection c, SqliteTransaction? tx, string w, Guid request) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT workspace_id,review_id,action,request_fingerprint,payload_hash FROM copyedit_proofreading_receipts WHERE workspace_id=$w AND request_id=$r;"; cmd.Parameters.AddWithValue("$w", w); cmd.Parameters.AddWithValue("$r", request.ToString("D")); using var x = cmd.ExecuteReader(); return x.Read() ? new(x.GetString(0), Guid.Parse(x.GetString(1)), x.GetString(2), x.GetString(3), x.GetString(4)) : null; }
    private static void RequireReceipt(Receipt r, string action, string workspace, Guid review, string fingerprint, string hash) { if (r.Workspace != workspace || r.ReviewId != review || r.Action != action || r.Fingerprint != fingerprint || r.Hash != hash) throw new CopyeditProofreadingConflictException("Request id reused with different payload."); }
    private static CopyeditProofreadingReview Require(SqliteConnection c, SqliteTransaction? tx, string w, Guid id) => Read(c, tx, w, id) ?? throw new CopyeditProofreadingValidationException("Review not found.");

    private static CopyeditProofreadingReview? Read(SqliteConnection c, SqliteTransaction? tx, string w, Guid id)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT project_id,editorial_plan_id,themes_pacing_review_id,expected_themes_pacing_revision,expected_themes_pacing_digest,version,rule_set,style_guide,language_tag,actor,snapshot_json,revision,status,decision,decision_reason,expected_repair_revision,message_id,created_at_utc,updated_at_utc FROM copyedit_proofreading_reviews WHERE workspace_id=$w AND review_id=$id;"; cmd.Parameters.AddWithValue("$w", w); cmd.Parameters.AddWithValue("$id", id.ToString("D")); using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
        var review = new { Project = Guid.Parse(r.GetString(0)), Plan = Guid.Parse(r.GetString(1)), Authority = Guid.Parse(r.GetString(2)), AuthorityRevision = r.GetInt64(3), Digest = r.GetString(4), Version = r.GetInt32(5), Rules = r.GetString(6), Style = r.GetString(7), Lang = r.GetString(8), Actor = r.GetString(9), Snapshot = r.GetString(10), Revision = r.GetInt64(11), Status = Enum.Parse<CopyeditProofreadingReviewStatus>(r.GetString(12), true), Decision = r.IsDBNull(13) ? (CopyeditProofreadingDecision?)null : Enum.Parse<CopyeditProofreadingDecision>(r.GetString(13), true), Reason = r.IsDBNull(14) ? null : r.GetString(14), Repair = r.IsDBNull(15) ? (int?)null : r.GetInt32(15), Message = r.IsDBNull(16) ? (Guid?)null : Guid.Parse(r.GetString(16)), Created = DateTimeOffset.Parse(r.GetString(17), CultureInfo.InvariantCulture), Updated = DateTimeOffset.Parse(r.GetString(18), CultureInfo.InvariantCulture) }; r.Close();
        var findings = new List<CopyeditProofreadingFinding>(); using var f = c.CreateCommand(); f.Transaction = tx; f.CommandText = "SELECT finding_id,area,severity,rule,location,chapter_numbers_json,scene_ids_json,paragraph_ids_json,spans_json,suggested_correction,evidence,is_open FROM copyedit_proofreading_findings WHERE workspace_id=$w AND review_id=$id ORDER BY finding_id;"; f.Parameters.AddWithValue("$w", w); f.Parameters.AddWithValue("$id", id.ToString("D")); using var fr = f.ExecuteReader(); while (fr.Read()) findings.Add(new(Guid.Parse(fr.GetString(0)), Enum.Parse<CopyeditProofreadingFindingArea>(fr.GetString(1), true), Enum.Parse<CopyeditProofreadingSeverity>(fr.GetString(2), true), fr.GetString(3), fr.GetString(4), JsonSerializer.Deserialize<List<int>>(fr.GetString(5)) ?? [], JsonSerializer.Deserialize<List<string>>(fr.GetString(6)) ?? [], JsonSerializer.Deserialize<List<string>>(fr.GetString(7)) ?? [], JsonSerializer.Deserialize<List<string>>(fr.GetString(8)) ?? [], fr.GetString(9), fr.GetString(10), fr.GetInt32(11) == 1));
        return new(id, review.Project, w, review.Plan, review.Authority, review.AuthorityRevision, review.Digest, review.Version, review.Rules, review.Style, review.Lang, review.Actor, review.Snapshot, review.Revision, review.Status, findings, review.Decision, review.Reason, review.Repair, review.Message, review.Created, review.Updated);
    }

    private static void UpdateReview(SqliteConnection c, SqliteTransaction tx, CopyeditProofreadingReview r, CopyeditProofreadingReviewStatus status, long revision, CopyeditProofreadingDecision? decision, string? reason, int? repair, Guid message, DateTimeOffset at) => Execute(c, tx, "UPDATE copyedit_proofreading_reviews SET revision=$r,status=$s,decision=$d,decision_reason=$reason,expected_repair_revision=$repair,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND review_id=$id;", ("$r", revision), ("$s", status.ToString().ToUpperInvariant()), ("$d", decision is null ? DBNull.Value : decision.Value.ToString().ToUpperInvariant()), ("$reason", reason is null ? DBNull.Value : reason), ("$repair", repair is null ? DBNull.Value : repair.Value), ("$m", message.ToString("D")), ("$at", Text(at)), ("$w", r.WorkspaceId), ("$id", r.ReviewId.ToString("D")));
    private static void InsertHistory(SqliteConnection c, SqliteTransaction tx, string w, Guid id, long rev, string action, string actor, string? reason, object payload, DateTimeOffset at) => Execute(c, tx, "INSERT INTO copyedit_proofreading_history(workspace_id,review_id,revision,action,actor,reason,payload_json,occurred_at_utc) VALUES($w,$id,$r,$a,$actor,$reason,$p,$at);", ("$w", w), ("$id", id.ToString("D")), ("$r", rev), ("$a", action), ("$actor", actor), ("$reason", reason is null ? DBNull.Value : reason), ("$p", JsonSerializer.Serialize(payload)), ("$at", Text(at)));
    private static void InsertReceipt(SqliteConnection c, SqliteTransaction tx, string w, Guid request, Guid id, string action, string fingerprint, string hash, long rev, Guid? message, DateTimeOffset at) => Execute(c, tx, "INSERT INTO copyedit_proofreading_receipts(workspace_id,request_id,review_id,action,request_fingerprint,payload_hash,resulting_revision,message_id,created_at_utc) VALUES($w,$r,$id,$a,$f,$h,$v,$m,$at);", ("$w", w), ("$r", request.ToString("D")), ("$id", id.ToString("D")), ("$a", action), ("$f", fingerprint), ("$h", hash), ("$v", rev), ("$m", message is null ? DBNull.Value : message.Value.ToString("D")), ("$at", Text(at)));
    private static void InsertOutbox(SqliteConnection c, SqliteTransaction tx, Guid message, string type, object payload, DateTimeOffset at) => Execute(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($id,$t,'1.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);", ("$id", message.ToString("D")), ("$t", type), ("$p", JsonSerializer.Serialize(payload)), ("$at", Text(at)));
    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] values) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var (n, v) in values) cmd.Parameters.AddWithValue(n, v); cmd.ExecuteNonQuery(); }
    private static Guid MessageId(Guid request) => new(SHA256.HashData(request.ToByteArray()).AsSpan(0, 16));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Text(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

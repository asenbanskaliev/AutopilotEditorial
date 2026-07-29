using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteVoiceLineEditingStore : IVoiceLineEditingStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteVoiceLineEditingStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<VoiceLineReviewCreateResult> CreateAsync(VoiceLineReviewDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(d);
        var hash = Hash(JsonSerializer.Serialize(new { d.ProjectId, d.WorkspaceId, d.EditorialPlanId, d.StructuralContentReviewId, d.ExpectedStructuralContentRevision, d.ExpectedStructuralContentDigest, d.Version, d.RuleSet, d.Actor, d.SnapshotJson }));
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, d.WorkspaceId, d.ReviewId);
            if (existing is not null)
            {
                var receipt = ReadReceipt(c, tx, d.WorkspaceId, d.ReviewId) ?? throw new VoiceLineEditingConflictException("Create receipt missing.");
                RequireReceipt(receipt, "CREATE", d.WorkspaceId, d.ReviewId, d.RequestFingerprint, hash);
                return new VoiceLineReviewCreateResult(existing, true);
            }

            RequireAuthority(c, tx, d.WorkspaceId, d.ProjectId, d.EditorialPlanId, d.StructuralContentReviewId, d.ExpectedStructuralContentRevision, d.ExpectedStructuralContentDigest);
            var message = MessageId(d.ReviewId);
            Execute(c, tx, "INSERT INTO voice_line_reviews(workspace_id,review_id,project_id,editorial_plan_id,structural_content_review_id,expected_structural_content_revision,expected_structural_content_digest,version,rule_set,actor,snapshot_json,revision,status,decision,decision_reason,expected_repair_revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$plan,$source,$r,$digest,$v,$rules,$actor,$snapshot,1,'PROPOSED',NULL,NULL,NULL,$m,$at,$at);",
                ("$w", d.WorkspaceId), ("$id", d.ReviewId.ToString("D")), ("$p", d.ProjectId.ToString("D")), ("$plan", d.EditorialPlanId.ToString("D")), ("$source", d.StructuralContentReviewId.ToString("D")), ("$r", d.ExpectedStructuralContentRevision), ("$digest", d.ExpectedStructuralContentDigest), ("$v", d.Version), ("$rules", d.RuleSet), ("$actor", d.Actor), ("$snapshot", d.SnapshotJson), ("$m", message.ToString("D")), ("$at", Text(at)));
            InsertHistory(c, tx, d.WorkspaceId, d.ReviewId, 1, "CREATE", d.Actor, null, new { d.ProjectId, d.EditorialPlanId, d.StructuralContentReviewId, d.ExpectedStructuralContentRevision, d.ExpectedStructuralContentDigest }, at);
            InsertReceipt(c, tx, d.WorkspaceId, d.ReviewId, d.ReviewId, "CREATE", d.RequestFingerprint, hash, 1, message, at);
            InsertOutbox(c, tx, message, "editorial.voice-line.proposed", new { d.WorkspaceId, d.ReviewId, d.ProjectId, d.EditorialPlanId, d.StructuralContentReviewId, d.Actor }, at);
            return new VoiceLineReviewCreateResult(Require(c, tx, d.WorkspaceId, d.ReviewId), false);
        }, ct);
    }

    public ValueTask<VoiceLineReview> EvaluateAsync(VoiceLineEvaluateCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (cmd.Findings is null || string.IsNullOrWhiteSpace(cmd.Evidence)) throw new VoiceLineEditingValidationException("Findings and evaluation evidence are required.");
        ValidateFindings(cmd.Findings);
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.ReviewId, "EVALUATE", cmd.RequestFingerprint,
            Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.ReviewId, cmd.ExpectedRevision, cmd.Findings, cmd.Evidence, cmd.Actor })), cmd.ExpectedRevision, at,
            (c, tx, review) =>
            {
                if (!AuthorityMatches(c, tx, review)) return MarkStaleCore(c, tx, review, cmd.RequestId, "Structural/content authority drift.", cmd.Actor, at);
                if (review.Status is not (VoiceLineReviewStatus.Proposed or VoiceLineReviewStatus.RepairRequired)) throw new VoiceLineEditingTransitionException("Review cannot be evaluated from its current state.");
                Execute(c, tx, "DELETE FROM voice_line_findings WHERE workspace_id=$w AND review_id=$id;", ("$w", review.WorkspaceId), ("$id", review.ReviewId.ToString("D")));
                foreach (var f in cmd.Findings)
                    Execute(c, tx, "INSERT INTO voice_line_findings(workspace_id,review_id,finding_id,area,severity,rule,location,chapter_numbers_json,scene_ids_json,paragraph_ids_json,spans_json,evidence,is_open) VALUES($w,$id,$fid,$a,$s,$r,$l,$c,$sc,$pa,$sp,$e,$o);",
                        ("$w", review.WorkspaceId), ("$id", review.ReviewId.ToString("D")), ("$fid", f.FindingId.ToString("D")), ("$a", DbArea(f.Area)), ("$s", DbSeverity(f.Severity)), ("$r", f.Rule), ("$l", f.Location), ("$c", JsonSerializer.Serialize(f.ChapterNumbers)), ("$sc", JsonSerializer.Serialize(f.SceneIds)), ("$pa", JsonSerializer.Serialize(f.ParagraphIds)), ("$sp", JsonSerializer.Serialize(f.Spans)), ("$e", f.Evidence), ("$o", f.IsOpen ? 1 : 0));
                var revision = review.Revision + 1;
                var message = MessageId(cmd.RequestId);
                UpdateReview(c, tx, review, VoiceLineReviewStatus.Evaluated, revision, null, null, null, message, at);
                InsertHistory(c, tx, review.WorkspaceId, review.ReviewId, revision, "EVALUATE", cmd.Actor, null, new { cmd.Evidence, FindingCount = cmd.Findings.Count }, at);
                InsertOutbox(c, tx, message, "editorial.voice-line.evaluated", new { review.WorkspaceId, review.ReviewId, FindingCount = cmd.Findings.Count, cmd.Actor }, at);
                return Require(c, tx, review.WorkspaceId, review.ReviewId);
            }, ct);
    }

    public ValueTask<VoiceLineReview> DecideAsync(VoiceLineDecisionCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new VoiceLineEditingValidationException("Decision reason is required.");
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.ReviewId, "DECIDE", cmd.RequestFingerprint,
            Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.ReviewId, cmd.ExpectedRevision, cmd.Decision, cmd.Reason, cmd.ExpectedRepairRevision, cmd.Actor })), cmd.ExpectedRevision, at,
            (c, tx, review) =>
            {
                if (!AuthorityMatches(c, tx, review)) return MarkStaleCore(c, tx, review, cmd.RequestId, "Structural/content authority drift.", cmd.Actor, at);
                if (review.Status != VoiceLineReviewStatus.Evaluated) throw new VoiceLineEditingTransitionException("Only an evaluated review can be decided.");
                if (cmd.Decision == VoiceLineDecision.Approve && review.Findings.Any(f => f.IsOpen && f.Severity == VoiceLineSeverity.Blocking)) throw new VoiceLineEditingTransitionException("Blocking findings prevent approval.");
                if (cmd.Decision == VoiceLineDecision.ReturnToRepair && cmd.ExpectedRepairRevision is null) throw new VoiceLineEditingValidationException("Repair revision is required.");
                var status = cmd.Decision switch { VoiceLineDecision.Approve => VoiceLineReviewStatus.Approved, VoiceLineDecision.Reject => VoiceLineReviewStatus.Rejected, VoiceLineDecision.ReturnToRepair => VoiceLineReviewStatus.RepairRequired, _ => throw new ArgumentOutOfRangeException() };
                var revision = review.Revision + 1;
                var message = MessageId(cmd.RequestId);
                UpdateReview(c, tx, review, status, revision, cmd.Decision, cmd.Reason, cmd.ExpectedRepairRevision, message, at);
                InsertHistory(c, tx, review.WorkspaceId, review.ReviewId, revision, "DECIDE", cmd.Actor, cmd.Reason, new { cmd.Decision, cmd.ExpectedRepairRevision }, at);
                InsertOutbox(c, tx, message, cmd.Decision switch { VoiceLineDecision.Approve => "editorial.voice-line.approved", VoiceLineDecision.Reject => "editorial.voice-line.rejected", _ => "editorial.voice-line.repair-required" }, new { review.WorkspaceId, review.ReviewId, cmd.Decision, cmd.Reason, cmd.Actor }, at);
                return Require(c, tx, review.WorkspaceId, review.ReviewId);
            }, ct);
    }

    public ValueTask<VoiceLineReview> ReopenAsync(VoiceLineReopenCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new VoiceLineEditingValidationException("Reopen reason is required.");
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.ReviewId, "REOPEN", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.ReviewId, cmd.ExpectedRevision, cmd.Reason, cmd.Actor })), cmd.ExpectedRevision, at,
            (c, tx, review) =>
            {
                if (review.Status is not (VoiceLineReviewStatus.Approved or VoiceLineReviewStatus.Rejected or VoiceLineReviewStatus.Stale)) throw new VoiceLineEditingTransitionException("Review cannot be reopened from its current state.");
                var revision = review.Revision + 1;
                var message = MessageId(cmd.RequestId);
                UpdateReview(c, tx, review, VoiceLineReviewStatus.Proposed, revision, null, null, null, message, at);
                InsertHistory(c, tx, review.WorkspaceId, review.ReviewId, revision, "REOPEN", cmd.Actor, cmd.Reason, new { cmd.Reason }, at);
                InsertOutbox(c, tx, message, "editorial.voice-line.reopened", new { review.WorkspaceId, review.ReviewId, cmd.Reason, cmd.Actor }, at);
                return Require(c, tx, review.WorkspaceId, review.ReviewId);
            }, ct);
    }

    public ValueTask<VoiceLineReview> MarkStaleAsync(VoiceLineStaleCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new VoiceLineEditingValidationException("Stale reason is required.");
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.ReviewId, "STALE", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.ReviewId, cmd.ExpectedRevision, cmd.Reason, cmd.Actor })), cmd.ExpectedRevision, at,
            (c, tx, review) => MarkStaleCore(c, tx, review, cmd.RequestId, cmd.Reason, cmd.Actor, at), ct);
    }

    public async ValueTask<VoiceLineReview?> GetAsync(string workspaceId, Guid reviewId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var c = _factory.OpenConnection();
        return await Task.FromResult(Read(c, null, workspaceId, reviewId));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false);
    }

    private ValueTask<VoiceLineReview> Mutate(Guid requestId, string workspace, Guid reviewId, string action, string fingerprint, string hash, long expectedRevision, DateTimeOffset at, Func<SqliteConnection, SqliteTransaction, VoiceLineReview, VoiceLineReview> mutate, CancellationToken ct) =>
        _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var receipt = ReadReceipt(c, tx, workspace, requestId);
            if (receipt is not null)
            {
                RequireReceipt(receipt, action, workspace, reviewId, fingerprint, hash);
                return Require(c, tx, workspace, reviewId);
            }
            var review = Require(c, tx, workspace, reviewId);
            if (review.Revision != expectedRevision) throw new VoiceLineEditingConflictException("Stale revision.");
            var result = mutate(c, tx, review);
            InsertReceipt(c, tx, workspace, requestId, reviewId, action, fingerprint, hash, result.Revision, result.MessageId, at);
            return result;
        }, ct);

    private static VoiceLineReview MarkStaleCore(SqliteConnection c, SqliteTransaction tx, VoiceLineReview review, Guid requestId, string reason, string actor, DateTimeOffset at)
    {
        if (review.Status == VoiceLineReviewStatus.Approved) throw new VoiceLineEditingTransitionException("Approved review must be reopened before stale transition.");
        var revision = review.Revision + 1;
        var message = MessageId(requestId);
        UpdateReview(c, tx, review, VoiceLineReviewStatus.Stale, revision, null, reason, null, message, at);
        InsertHistory(c, tx, review.WorkspaceId, review.ReviewId, revision, "STALE", actor, reason, new { reason }, at);
        InsertOutbox(c, tx, message, "editorial.voice-line.stale", new { review.WorkspaceId, review.ReviewId, review.ProjectId, Reason = reason, Actor = actor }, at);
        return Require(c, tx, review.WorkspaceId, review.ReviewId);
    }

    private static void ValidateDraft(VoiceLineReviewDraft d)
    {
        if (d.ReviewId == Guid.Empty || d.ProjectId == Guid.Empty || d.EditorialPlanId == Guid.Empty || d.StructuralContentReviewId == Guid.Empty || d.ExpectedStructuralContentRevision < 1 || d.Version < 1 || string.IsNullOrWhiteSpace(d.WorkspaceId) || string.IsNullOrWhiteSpace(d.ExpectedStructuralContentDigest) || string.IsNullOrWhiteSpace(d.RuleSet) || string.IsNullOrWhiteSpace(d.Actor) || string.IsNullOrWhiteSpace(d.SnapshotJson) || string.IsNullOrWhiteSpace(d.RequestFingerprint)) throw new VoiceLineEditingValidationException("Complete voice/line review is required.");
    }

    private static void ValidateFindings(IReadOnlyList<VoiceLineFindingDraft> findings)
    {
        if (findings.Select(x => x.FindingId).Distinct().Count() != findings.Count) throw new VoiceLineEditingValidationException("Finding ids must be unique.");
        foreach (var f in findings)
            if (f.FindingId == Guid.Empty || string.IsNullOrWhiteSpace(f.Rule) || string.IsNullOrWhiteSpace(f.Location) || string.IsNullOrWhiteSpace(f.Evidence) || f.ChapterNumbers is null || f.SceneIds is null || f.ParagraphIds is null || f.Spans is null || f.ChapterNumbers.Any(x => x < 1) || f.SceneIds.Any(string.IsNullOrWhiteSpace) || f.ParagraphIds.Any(string.IsNullOrWhiteSpace) || f.Spans.Any(string.IsNullOrWhiteSpace)) throw new VoiceLineEditingValidationException("Complete finding evidence is required.");
    }

    private static void RequireAuthority(SqliteConnection c, SqliteTransaction tx, string workspace, Guid project, Guid plan, Guid sourceReview, long expectedRevision, string expectedDigest)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT r.project_id,r.editorial_plan_id,r.revision,r.status,n.status FROM structural_content_reviews r JOIN editorial_pass_nodes n ON n.workspace_id=r.workspace_id AND n.plan_id=r.editorial_plan_id WHERE r.workspace_id=$w AND r.review_id=$id AND n.pass_kind='VOICELINE';";
        cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$id", sourceReview.ToString("D"));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new VoiceLineEditingValidationException("Approved structural/content authority was not found.");
        var storedProject = Guid.Parse(reader.GetString(0)); var storedPlan = Guid.Parse(reader.GetString(1)); var revision = reader.GetInt64(2); var status = reader.GetString(3); var nodeStatus = reader.GetString(4);
        var digest = Hash($"{workspace}:{sourceReview:D}:{revision}:{status}");
        if (storedProject != project || storedPlan != plan || revision != expectedRevision || status != "APPROVED" || nodeStatus is not ("READY" or "IN_PROGRESS") || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(digest), Encoding.UTF8.GetBytes(expectedDigest))) throw new VoiceLineEditingValidationException("Structural/content review authority is not approved, exact, current, or dependency-ready.");
    }

    private static bool AuthorityMatches(SqliteConnection c, SqliteTransaction tx, VoiceLineReview r)
    {
        try { RequireAuthority(c, tx, r.WorkspaceId, r.ProjectId, r.EditorialPlanId, r.StructuralContentReviewId, r.ExpectedStructuralContentRevision, r.ExpectedStructuralContentDigest); return true; }
        catch (VoiceLineEditingValidationException) { return false; }
    }

    private sealed record Receipt(string Workspace, Guid ReviewId, string Action, string Fingerprint, string Hash);

    private static Receipt? ReadReceipt(SqliteConnection c, SqliteTransaction? tx, string workspace, Guid requestId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT workspace_id,review_id,action,request_fingerprint,payload_hash FROM voice_line_receipts WHERE workspace_id=$w AND request_id=$r;";
        cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$r", requestId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4)) : null;
    }

    private static void RequireReceipt(Receipt r, string action, string workspace, Guid review, string fingerprint, string hash)
    {
        if (r.Workspace != workspace || r.ReviewId != review || r.Action != action || r.Fingerprint != fingerprint || r.Hash != hash) throw new VoiceLineEditingConflictException("Request id reused with different payload.");
    }

    private static VoiceLineReview Require(SqliteConnection c, SqliteTransaction? tx, string workspace, Guid reviewId) => Read(c, tx, workspace, reviewId) ?? throw new VoiceLineEditingValidationException("Voice/line review not found.");

    private static VoiceLineReview? Read(SqliteConnection c, SqliteTransaction? tx, string workspace, Guid reviewId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT project_id,editorial_plan_id,structural_content_review_id,expected_structural_content_revision,expected_structural_content_digest,version,rule_set,actor,snapshot_json,revision,status,decision,decision_reason,expected_repair_revision,message_id,created_at_utc,updated_at_utc FROM voice_line_reviews WHERE workspace_id=$w AND review_id=$id;";
        cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$id", reviewId.ToString("D"));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var project = Guid.Parse(r.GetString(0)); var plan = Guid.Parse(r.GetString(1)); var source = Guid.Parse(r.GetString(2)); var sourceRevision = r.GetInt64(3); var digest = r.GetString(4); var version = r.GetInt32(5); var ruleSet = r.GetString(6); var actor = r.GetString(7); var snapshot = r.GetString(8); var revision = r.GetInt64(9); var status = ParseStatus(r.GetString(10)); VoiceLineDecision? decision = r.IsDBNull(11) ? null : ParseDecision(r.GetString(11)); var reason = r.IsDBNull(12) ? null : r.GetString(12); int? repairRevision = r.IsDBNull(13) ? null : r.GetInt32(13); Guid? message = r.IsDBNull(14) ? null : Guid.Parse(r.GetString(14)); var created = DateTimeOffset.Parse(r.GetString(15), CultureInfo.InvariantCulture); var updated = DateTimeOffset.Parse(r.GetString(16), CultureInfo.InvariantCulture);
        r.Close();
        var findings = new List<VoiceLineFinding>();
        using var f = c.CreateCommand(); f.Transaction = tx;
        f.CommandText = "SELECT finding_id,area,severity,rule,location,chapter_numbers_json,scene_ids_json,paragraph_ids_json,spans_json,evidence,is_open FROM voice_line_findings WHERE workspace_id=$w AND review_id=$id ORDER BY finding_id;";
        f.Parameters.AddWithValue("$w", workspace); f.Parameters.AddWithValue("$id", reviewId.ToString("D"));
        using var fr = f.ExecuteReader();
        while (fr.Read()) findings.Add(new(Guid.Parse(fr.GetString(0)), ParseArea(fr.GetString(1)), ParseSeverity(fr.GetString(2)), fr.GetString(3), fr.GetString(4), JsonSerializer.Deserialize<List<int>>(fr.GetString(5)) ?? [], JsonSerializer.Deserialize<List<string>>(fr.GetString(6)) ?? [], JsonSerializer.Deserialize<List<string>>(fr.GetString(7)) ?? [], JsonSerializer.Deserialize<List<string>>(fr.GetString(8)) ?? [], fr.GetString(9), fr.GetInt32(10) == 1));
        return new(reviewId, project, workspace, plan, source, sourceRevision, digest, version, ruleSet, actor, snapshot, revision, status, findings, decision, reason, repairRevision, message, created, updated);
    }

    private static void UpdateReview(SqliteConnection c, SqliteTransaction tx, VoiceLineReview r, VoiceLineReviewStatus status, long revision, VoiceLineDecision? decision, string? reason, int? repairRevision, Guid message, DateTimeOffset at) =>
        Execute(c, tx, "UPDATE voice_line_reviews SET revision=$r,status=$s,decision=$d,decision_reason=$reason,expected_repair_revision=$repair,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND review_id=$id;", ("$r", revision), ("$s", DbStatus(status)), ("$d", decision is null ? DBNull.Value : DbDecision(decision.Value)), ("$reason", reason is null ? DBNull.Value : reason), ("$repair", repairRevision is null ? DBNull.Value : repairRevision.Value), ("$m", message.ToString("D")), ("$at", Text(at)), ("$w", r.WorkspaceId), ("$id", r.ReviewId.ToString("D")));

    private static void InsertHistory(SqliteConnection c, SqliteTransaction tx, string workspace, Guid review, long revision, string action, string actor, string? reason, object payload, DateTimeOffset at) => Execute(c, tx, "INSERT INTO voice_line_history(workspace_id,review_id,revision,action,actor,reason,payload_json,occurred_at_utc) VALUES($w,$id,$r,$a,$actor,$reason,$p,$at);", ("$w", workspace), ("$id", review.ToString("D")), ("$r", revision), ("$a", action), ("$actor", actor), ("$reason", reason is null ? DBNull.Value : reason), ("$p", JsonSerializer.Serialize(payload)), ("$at", Text(at)));
    private static void InsertReceipt(SqliteConnection c, SqliteTransaction tx, string workspace, Guid request, Guid review, string action, string fingerprint, string hash, long revision, Guid? message, DateTimeOffset at) => Execute(c, tx, "INSERT INTO voice_line_receipts(workspace_id,request_id,review_id,action,request_fingerprint,payload_hash,resulting_revision,message_id,created_at_utc) VALUES($w,$r,$id,$a,$f,$h,$v,$m,$at);", ("$w", workspace), ("$r", request.ToString("D")), ("$id", review.ToString("D")), ("$a", action), ("$f", fingerprint), ("$h", hash), ("$v", revision), ("$m", message is null ? DBNull.Value : message.Value.ToString("D")), ("$at", Text(at)));
    private static void InsertOutbox(SqliteConnection c, SqliteTransaction tx, Guid message, string type, object payload, DateTimeOffset at) => Execute(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($id,$t,'1.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);", ("$id", message.ToString("D")), ("$t", type), ("$p", JsonSerializer.Serialize(payload)), ("$at", Text(at)));

    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] values)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static string DbStatus(VoiceLineReviewStatus s) => s switch { VoiceLineReviewStatus.Proposed => "PROPOSED", VoiceLineReviewStatus.Evaluated => "EVALUATED", VoiceLineReviewStatus.Approved => "APPROVED", VoiceLineReviewStatus.Rejected => "REJECTED", VoiceLineReviewStatus.RepairRequired => "REPAIR_REQUIRED", VoiceLineReviewStatus.Stale => "STALE", _ => throw new ArgumentOutOfRangeException(nameof(s)) };
    private static VoiceLineReviewStatus ParseStatus(string s) => s switch { "PROPOSED" => VoiceLineReviewStatus.Proposed, "EVALUATED" => VoiceLineReviewStatus.Evaluated, "APPROVED" => VoiceLineReviewStatus.Approved, "REJECTED" => VoiceLineReviewStatus.Rejected, "REPAIR_REQUIRED" => VoiceLineReviewStatus.RepairRequired, "STALE" => VoiceLineReviewStatus.Stale, _ => throw new InvalidOperationException(s) };
    private static string DbDecision(VoiceLineDecision d) => d switch { VoiceLineDecision.Approve => "APPROVE", VoiceLineDecision.Reject => "REJECT", VoiceLineDecision.ReturnToRepair => "RETURN_TO_REPAIR", _ => throw new ArgumentOutOfRangeException(nameof(d)) };
    private static VoiceLineDecision ParseDecision(string d) => d switch { "APPROVE" => VoiceLineDecision.Approve, "REJECT" => VoiceLineDecision.Reject, "RETURN_TO_REPAIR" => VoiceLineDecision.ReturnToRepair, _ => throw new InvalidOperationException(d) };
    private static string DbSeverity(VoiceLineSeverity s) => s.ToString().ToUpperInvariant();
    private static VoiceLineSeverity ParseSeverity(string s) => Enum.Parse<VoiceLineSeverity>(s, true);
    private static string DbArea(VoiceLineFindingArea a) => a.ToString().ToUpperInvariant();
    private static VoiceLineFindingArea ParseArea(string a) => Enum.Parse<VoiceLineFindingArea>(a, true);
    private static Guid MessageId(Guid request) => new(SHA256.HashData(request.ToByteArray()).AsSpan(0, 16));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Text(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

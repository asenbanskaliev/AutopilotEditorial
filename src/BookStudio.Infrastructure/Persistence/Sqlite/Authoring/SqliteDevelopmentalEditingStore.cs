using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteDevelopmentalEditingStore : IDevelopmentalEditingStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteDevelopmentalEditingStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<DevelopmentalReviewCreateResult> CreateAsync(DevelopmentalReviewDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(d);
        var hash = Hash(JsonSerializer.Serialize(new { d.ProjectId, d.WorkspaceId, d.EditorialPlanId, d.ExpectedPlanRevision, d.ExpectedPlanDigest, d.Version, d.RuleSet, d.Actor, d.SnapshotJson }));
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, d.WorkspaceId, d.ReviewId);
            if (existing is not null)
            {
                var receipt = ReadReceipt(c, tx, d.WorkspaceId, d.ReviewId) ?? throw new DevelopmentalEditingConflictException("Create receipt missing.");
                RequireReceipt(receipt, "CREATE", d.WorkspaceId, d.ReviewId, d.RequestFingerprint, hash);
                return new DevelopmentalReviewCreateResult(existing, true);
            }

            RequireAuthority(c, tx, d.WorkspaceId, d.ProjectId, d.EditorialPlanId, d.ExpectedPlanRevision, d.ExpectedPlanDigest);
            var message = MessageId(d.ReviewId);
            Execute(c, tx, "INSERT INTO developmental_reviews(workspace_id,review_id,project_id,editorial_plan_id,expected_plan_revision,expected_plan_digest,version,rule_set,actor,snapshot_json,revision,status,decision,decision_reason,expected_repair_revision,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$plan,$r,$digest,$v,$rules,$actor,$snapshot,1,'PROPOSED',NULL,NULL,NULL,$m,$at,$at);",
                ("$w", d.WorkspaceId), ("$id", d.ReviewId.ToString("D")), ("$p", d.ProjectId.ToString("D")), ("$plan", d.EditorialPlanId.ToString("D")), ("$r", d.ExpectedPlanRevision), ("$digest", d.ExpectedPlanDigest), ("$v", d.Version), ("$rules", d.RuleSet), ("$actor", d.Actor), ("$snapshot", d.SnapshotJson), ("$m", message.ToString("D")), ("$at", Text(at)));
            InsertHistory(c, tx, d.WorkspaceId, d.ReviewId, 1, "CREATE", d.Actor, null, new { d.ProjectId, d.EditorialPlanId, d.ExpectedPlanRevision, d.ExpectedPlanDigest }, at);
            InsertReceipt(c, tx, d.WorkspaceId, d.ReviewId, d.ReviewId, "CREATE", d.RequestFingerprint, hash, 1, message, at);
            InsertOutbox(c, tx, message, "editorial.developmental.proposed", new { d.WorkspaceId, d.ReviewId, d.ProjectId, d.EditorialPlanId, d.Actor }, at);
            return new DevelopmentalReviewCreateResult(Require(c, tx, d.WorkspaceId, d.ReviewId), false);
        }, ct);
    }

    public ValueTask<DevelopmentalReview> EvaluateAsync(DevelopmentalEvaluateCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (cmd.Findings is null || string.IsNullOrWhiteSpace(cmd.Evidence)) throw new DevelopmentalEditingValidationException("Findings and evaluation evidence are required.");
        ValidateFindings(cmd.Findings);
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.ReviewId, "EVALUATE", cmd.RequestFingerprint,
            Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.ReviewId, cmd.ExpectedRevision, cmd.Findings, cmd.Evidence, cmd.Actor })), cmd.ExpectedRevision, at,
            (c, tx, review) =>
            {
                if (!AuthorityMatches(c, tx, review)) return MarkStaleCore(c, tx, review, cmd.RequestId, "Editorial plan authority drift.", cmd.Actor, at);
                if (review.Status is not (DevelopmentalReviewStatus.Proposed or DevelopmentalReviewStatus.RepairRequired)) throw new DevelopmentalEditingTransitionException("Review cannot be evaluated from its current state.");
                Execute(c, tx, "DELETE FROM developmental_findings WHERE workspace_id=$w AND review_id=$id;", ("$w", review.WorkspaceId), ("$id", review.ReviewId.ToString("D")));
                foreach (var f in cmd.Findings)
                    Execute(c, tx, "INSERT INTO developmental_findings(workspace_id,review_id,finding_id,area,severity,rule,location,chapter_numbers_json,evidence,is_open) VALUES($w,$id,$fid,$a,$s,$r,$l,$c,$e,$o);",
                        ("$w", review.WorkspaceId), ("$id", review.ReviewId.ToString("D")), ("$fid", f.FindingId.ToString("D")), ("$a", DbArea(f.Area)), ("$s", DbSeverity(f.Severity)), ("$r", f.Rule), ("$l", f.Location), ("$c", JsonSerializer.Serialize(f.ChapterNumbers)), ("$e", f.Evidence), ("$o", f.IsOpen ? 1 : 0));
                var revision = review.Revision + 1;
                var message = MessageId(cmd.RequestId);
                UpdateReview(c, tx, review, DevelopmentalReviewStatus.Evaluated, revision, null, null, null, message, at);
                InsertHistory(c, tx, review.WorkspaceId, review.ReviewId, revision, "EVALUATE", cmd.Actor, null, new { cmd.Evidence, FindingCount = cmd.Findings.Count }, at);
                InsertOutbox(c, tx, message, "editorial.developmental.evaluated", new { review.WorkspaceId, review.ReviewId, FindingCount = cmd.Findings.Count, cmd.Actor }, at);
                return Require(c, tx, review.WorkspaceId, review.ReviewId);
            }, ct);
    }

    public ValueTask<DevelopmentalReview> DecideAsync(DevelopmentalDecisionCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new DevelopmentalEditingValidationException("Decision reason is required.");
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.ReviewId, "DECIDE", cmd.RequestFingerprint,
            Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.ReviewId, cmd.ExpectedRevision, cmd.Decision, cmd.Reason, cmd.ExpectedRepairRevision, cmd.Actor })), cmd.ExpectedRevision, at,
            (c, tx, review) =>
            {
                if (!AuthorityMatches(c, tx, review)) return MarkStaleCore(c, tx, review, cmd.RequestId, "Editorial plan authority drift.", cmd.Actor, at);
                if (review.Status != DevelopmentalReviewStatus.Evaluated) throw new DevelopmentalEditingTransitionException("Only an evaluated review can be decided.");
                if (cmd.Decision == DevelopmentalDecision.Approve && review.Findings.Any(f => f.IsOpen && f.Severity == DevelopmentalSeverity.Blocking)) throw new DevelopmentalEditingTransitionException("Blocking findings prevent approval.");
                if (cmd.Decision == DevelopmentalDecision.ReturnToRepair && cmd.ExpectedRepairRevision is null) throw new DevelopmentalEditingValidationException("Repair revision is required.");
                var status = cmd.Decision switch { DevelopmentalDecision.Approve => DevelopmentalReviewStatus.Approved, DevelopmentalDecision.Reject => DevelopmentalReviewStatus.Rejected, DevelopmentalDecision.ReturnToRepair => DevelopmentalReviewStatus.RepairRequired, _ => throw new ArgumentOutOfRangeException() };
                var revision = review.Revision + 1;
                var message = MessageId(cmd.RequestId);
                UpdateReview(c, tx, review, status, revision, cmd.Decision, cmd.Reason, cmd.ExpectedRepairRevision, message, at);
                InsertHistory(c, tx, review.WorkspaceId, review.ReviewId, revision, "DECIDE", cmd.Actor, cmd.Reason, new { cmd.Decision, cmd.ExpectedRepairRevision }, at);
                InsertOutbox(c, tx, message, cmd.Decision switch { DevelopmentalDecision.Approve => "editorial.developmental.approved", DevelopmentalDecision.Reject => "editorial.developmental.rejected", _ => "editorial.developmental.repair-required" }, new { review.WorkspaceId, review.ReviewId, cmd.Decision, cmd.Reason, cmd.Actor }, at);
                return Require(c, tx, review.WorkspaceId, review.ReviewId);
            }, ct);
    }

    public ValueTask<DevelopmentalReview> ReopenAsync(DevelopmentalReopenCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new DevelopmentalEditingValidationException("Reopen reason is required.");
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.ReviewId, "REOPEN", cmd.RequestFingerprint,
            Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.ReviewId, cmd.ExpectedRevision, cmd.Reason, cmd.Actor })), cmd.ExpectedRevision, at,
            (c, tx, review) =>
            {
                if (review.Status is not (DevelopmentalReviewStatus.Approved or DevelopmentalReviewStatus.Rejected or DevelopmentalReviewStatus.Stale)) throw new DevelopmentalEditingTransitionException("Review cannot be reopened from its current state.");
                var revision = review.Revision + 1;
                var message = MessageId(cmd.RequestId);
                UpdateReview(c, tx, review, DevelopmentalReviewStatus.Proposed, revision, null, null, null, message, at);
                InsertHistory(c, tx, review.WorkspaceId, review.ReviewId, revision, "REOPEN", cmd.Actor, cmd.Reason, new { cmd.Reason }, at);
                InsertOutbox(c, tx, message, "editorial.developmental.reopened", new { review.WorkspaceId, review.ReviewId, cmd.Reason, cmd.Actor }, at);
                return Require(c, tx, review.WorkspaceId, review.ReviewId);
            }, ct);
    }

    public ValueTask<DevelopmentalReview> MarkStaleAsync(DevelopmentalStaleCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new DevelopmentalEditingValidationException("Stale reason is required.");
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.ReviewId, "STALE", cmd.RequestFingerprint,
            Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.ReviewId, cmd.ExpectedRevision, cmd.Reason, cmd.Actor })), cmd.ExpectedRevision, at,
            (c, tx, review) => MarkStaleCore(c, tx, review, cmd.RequestId, cmd.Reason, cmd.Actor, at), ct);
    }

    public async ValueTask<DevelopmentalReview?> GetAsync(string workspaceId, Guid reviewId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var c = _factory.OpenConnection();
        return await Task.FromResult(Read(c, null, workspaceId, reviewId));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false);
    }

    private ValueTask<DevelopmentalReview> Mutate(Guid requestId, string workspace, Guid reviewId, string action, string fingerprint, string hash, long expectedRevision, DateTimeOffset at, Func<SqliteConnection, SqliteTransaction, DevelopmentalReview, DevelopmentalReview> mutate, CancellationToken ct) =>
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
            if (review.Revision != expectedRevision) throw new DevelopmentalEditingConflictException("Stale revision.");
            var result = mutate(c, tx, review);
            InsertReceipt(c, tx, workspace, requestId, reviewId, action, fingerprint, hash, result.Revision, result.MessageId, at);
            return result;
        }, ct);

    private static DevelopmentalReview MarkStaleCore(SqliteConnection c, SqliteTransaction tx, DevelopmentalReview review, Guid requestId, string reason, string actor, DateTimeOffset at)
    {
        if (review.Status == DevelopmentalReviewStatus.Approved) throw new DevelopmentalEditingTransitionException("Approved review must be reopened before stale transition.");
        var revision = review.Revision + 1;
        var message = MessageId(requestId);
        UpdateReview(c, tx, review, DevelopmentalReviewStatus.Stale, revision, null, reason, null, message, at);
        InsertHistory(c, tx, review.WorkspaceId, review.ReviewId, revision, "STALE", actor, reason, new { reason }, at);
        InsertOutbox(c, tx, message, "editorial.developmental.stale", new { review.WorkspaceId, review.ReviewId, review.ProjectId, Reason = reason, Actor = actor }, at);
        return Require(c, tx, review.WorkspaceId, review.ReviewId);
    }

    private static void ValidateDraft(DevelopmentalReviewDraft d)
    {
        if (d.ReviewId == Guid.Empty || d.ProjectId == Guid.Empty || d.EditorialPlanId == Guid.Empty || d.ExpectedPlanRevision < 1 || d.Version < 1 || string.IsNullOrWhiteSpace(d.WorkspaceId) || string.IsNullOrWhiteSpace(d.ExpectedPlanDigest) || string.IsNullOrWhiteSpace(d.RuleSet) || string.IsNullOrWhiteSpace(d.Actor) || string.IsNullOrWhiteSpace(d.SnapshotJson) || string.IsNullOrWhiteSpace(d.RequestFingerprint)) throw new DevelopmentalEditingValidationException("Complete developmental review is required.");
    }

    private static void ValidateFindings(IReadOnlyList<DevelopmentalFindingDraft> findings)
    {
        if (findings.Select(x => x.FindingId).Distinct().Count() != findings.Count) throw new DevelopmentalEditingValidationException("Finding ids must be unique.");
        foreach (var f in findings)
            if (f.FindingId == Guid.Empty || string.IsNullOrWhiteSpace(f.Rule) || string.IsNullOrWhiteSpace(f.Location) || string.IsNullOrWhiteSpace(f.Evidence) || f.ChapterNumbers is null || f.ChapterNumbers.Any(x => x < 1)) throw new DevelopmentalEditingValidationException("Complete finding evidence is required.");
    }

    private static void RequireAuthority(SqliteConnection c, SqliteTransaction tx, string workspace, Guid project, Guid plan, long revision, string digest)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM editorial_pass_plans p JOIN editorial_pass_nodes n ON n.workspace_id=p.workspace_id AND n.plan_id=p.plan_id WHERE p.workspace_id=$w AND p.project_id=$p AND p.plan_id=$plan AND p.revision=$r AND n.pass_kind='DEVELOPMENTAL' AND n.status='IN_PROGRESS' AND lower(hex(sha3(p.workspace_id || ':' || p.plan_id || ':' || p.revision || ':' || p.status,256)))=$d;";
        cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$p", project.ToString("D")); cmd.Parameters.AddWithValue("$plan", plan.ToString("D")); cmd.Parameters.AddWithValue("$r", revision); cmd.Parameters.AddWithValue("$d", digest);
        try
        {
            if (cmd.ExecuteScalar() is null) throw new DevelopmentalEditingValidationException("Exact active developmental editorial pass was not found.");
        }
        catch (SqliteException)
        {
            using var fallback = c.CreateCommand(); fallback.Transaction = tx;
            fallback.CommandText = "SELECT 1 FROM editorial_pass_plans p JOIN editorial_pass_nodes n ON n.workspace_id=p.workspace_id AND n.plan_id=p.plan_id WHERE p.workspace_id=$w AND p.project_id=$p AND p.plan_id=$plan AND p.revision=$r AND n.pass_kind='DEVELOPMENTAL' AND n.status='IN_PROGRESS';";
            fallback.Parameters.AddWithValue("$w", workspace); fallback.Parameters.AddWithValue("$p", project.ToString("D")); fallback.Parameters.AddWithValue("$plan", plan.ToString("D")); fallback.Parameters.AddWithValue("$r", revision);
            if (fallback.ExecuteScalar() is null) throw new DevelopmentalEditingValidationException("Exact active developmental editorial pass was not found.");
        }
    }

    private static bool AuthorityMatches(SqliteConnection c, SqliteTransaction tx, DevelopmentalReview r)
    {
        try { RequireAuthority(c, tx, r.WorkspaceId, r.ProjectId, r.EditorialPlanId, r.ExpectedPlanRevision, r.ExpectedPlanDigest); return true; }
        catch (DevelopmentalEditingValidationException) { return false; }
    }

    private sealed record Receipt(string Workspace, Guid ReviewId, string Action, string Fingerprint, string Hash);

    private static Receipt? ReadReceipt(SqliteConnection c, SqliteTransaction? tx, string workspace, Guid requestId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT workspace_id,review_id,action,request_fingerprint,payload_hash FROM developmental_receipts WHERE workspace_id=$w AND request_id=$r;";
        cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$r", requestId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4)) : null;
    }

    private static void RequireReceipt(Receipt r, string action, string workspace, Guid review, string fingerprint, string hash)
    {
        if (r.Workspace != workspace || r.ReviewId != review || r.Action != action || r.Fingerprint != fingerprint || r.Hash != hash) throw new DevelopmentalEditingConflictException("Request id reused with different payload.");
    }

    private static DevelopmentalReview Require(SqliteConnection c, SqliteTransaction? tx, string workspace, Guid reviewId) => Read(c, tx, workspace, reviewId) ?? throw new DevelopmentalEditingValidationException("Developmental review not found.");

    private static DevelopmentalReview? Read(SqliteConnection c, SqliteTransaction? tx, string workspace, Guid reviewId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT project_id,editorial_plan_id,expected_plan_revision,expected_plan_digest,version,rule_set,actor,snapshot_json,revision,status,decision,decision_reason,expected_repair_revision,message_id,created_at_utc,updated_at_utc FROM developmental_reviews WHERE workspace_id=$w AND review_id=$id;";
        cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$id", reviewId.ToString("D"));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var project = Guid.Parse(r.GetString(0)); var plan = Guid.Parse(r.GetString(1)); var planRevision = r.GetInt64(2); var digest = r.GetString(3); var version = r.GetInt32(4); var ruleSet = r.GetString(5); var actor = r.GetString(6); var snapshot = r.GetString(7); var revision = r.GetInt64(8); var status = ParseStatus(r.GetString(9)); DevelopmentalDecision? decision = r.IsDBNull(10) ? null : ParseDecision(r.GetString(10)); var reason = r.IsDBNull(11) ? null : r.GetString(11); int? repairRevision = r.IsDBNull(12) ? null : r.GetInt32(12); Guid? message = r.IsDBNull(13) ? null : Guid.Parse(r.GetString(13)); var created = DateTimeOffset.Parse(r.GetString(14), CultureInfo.InvariantCulture); var updated = DateTimeOffset.Parse(r.GetString(15), CultureInfo.InvariantCulture);
        r.Close();
        var findings = new List<DevelopmentalFinding>();
        using var f = c.CreateCommand(); f.Transaction = tx;
        f.CommandText = "SELECT finding_id,area,severity,rule,location,chapter_numbers_json,evidence,is_open FROM developmental_findings WHERE workspace_id=$w AND review_id=$id ORDER BY finding_id;";
        f.Parameters.AddWithValue("$w", workspace); f.Parameters.AddWithValue("$id", reviewId.ToString("D"));
        using var fr = f.ExecuteReader();
        while (fr.Read()) findings.Add(new(Guid.Parse(fr.GetString(0)), ParseArea(fr.GetString(1)), ParseSeverity(fr.GetString(2)), fr.GetString(3), fr.GetString(4), JsonSerializer.Deserialize<List<int>>(fr.GetString(5)) ?? [], fr.GetString(6), fr.GetInt32(7) == 1));
        return new(reviewId, project, workspace, plan, planRevision, digest, version, ruleSet, actor, snapshot, revision, status, findings, decision, reason, repairRevision, message, created, updated);
    }

    private static void UpdateReview(SqliteConnection c, SqliteTransaction tx, DevelopmentalReview r, DevelopmentalReviewStatus status, long revision, DevelopmentalDecision? decision, string? reason, int? repairRevision, Guid message, DateTimeOffset at) =>
        Execute(c, tx, "UPDATE developmental_reviews SET revision=$r,status=$s,decision=$d,decision_reason=$reason,expected_repair_revision=$repair,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND review_id=$id;", ("$r", revision), ("$s", DbStatus(status)), ("$d", decision is null ? DBNull.Value : DbDecision(decision.Value)), ("$reason", reason is null ? DBNull.Value : reason), ("$repair", repairRevision is null ? DBNull.Value : repairRevision.Value), ("$m", message.ToString("D")), ("$at", Text(at)), ("$w", r.WorkspaceId), ("$id", r.ReviewId.ToString("D")));

    private static void InsertHistory(SqliteConnection c, SqliteTransaction tx, string workspace, Guid review, long revision, string action, string actor, string? reason, object payload, DateTimeOffset at) =>
        Execute(c, tx, "INSERT INTO developmental_history(workspace_id,review_id,revision,action,actor,reason,payload_json,occurred_at_utc) VALUES($w,$id,$r,$a,$actor,$reason,$p,$at);", ("$w", workspace), ("$id", review.ToString("D")), ("$r", revision), ("$a", action), ("$actor", actor), ("$reason", reason is null ? DBNull.Value : reason), ("$p", JsonSerializer.Serialize(payload)), ("$at", Text(at)));

    private static void InsertReceipt(SqliteConnection c, SqliteTransaction tx, string workspace, Guid request, Guid review, string action, string fingerprint, string hash, long revision, Guid? message, DateTimeOffset at) =>
        Execute(c, tx, "INSERT INTO developmental_receipts(workspace_id,request_id,review_id,action,request_fingerprint,payload_hash,resulting_revision,message_id,created_at_utc) VALUES($w,$r,$id,$a,$f,$h,$v,$m,$at);", ("$w", workspace), ("$r", request.ToString("D")), ("$id", review.ToString("D")), ("$a", action), ("$f", fingerprint), ("$h", hash), ("$v", revision), ("$m", message is null ? DBNull.Value : message.Value.ToString("D")), ("$at", Text(at)));

    private static void InsertOutbox(SqliteConnection c, SqliteTransaction tx, Guid message, string type, object payload, DateTimeOffset at) =>
        Execute(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($id,$t,'1.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);", ("$id", message.ToString("D")), ("$t", type), ("$p", JsonSerializer.Serialize(payload)), ("$at", Text(at)));

    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] values)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static string DbStatus(DevelopmentalReviewStatus s) => s switch { DevelopmentalReviewStatus.Proposed => "PROPOSED", DevelopmentalReviewStatus.Evaluated => "EVALUATED", DevelopmentalReviewStatus.Approved => "APPROVED", DevelopmentalReviewStatus.Rejected => "REJECTED", DevelopmentalReviewStatus.RepairRequired => "REPAIR_REQUIRED", DevelopmentalReviewStatus.Stale => "STALE", _ => throw new ArgumentOutOfRangeException(nameof(s)) };
    private static DevelopmentalReviewStatus ParseStatus(string s) => s switch { "PROPOSED" => DevelopmentalReviewStatus.Proposed, "EVALUATED" => DevelopmentalReviewStatus.Evaluated, "APPROVED" => DevelopmentalReviewStatus.Approved, "REJECTED" => DevelopmentalReviewStatus.Rejected, "REPAIR_REQUIRED" => DevelopmentalReviewStatus.RepairRequired, "STALE" => DevelopmentalReviewStatus.Stale, _ => throw new InvalidOperationException(s) };
    private static string DbDecision(DevelopmentalDecision d) => d switch { DevelopmentalDecision.Approve => "APPROVE", DevelopmentalDecision.Reject => "REJECT", DevelopmentalDecision.ReturnToRepair => "RETURN_TO_REPAIR", _ => throw new ArgumentOutOfRangeException(nameof(d)) };
    private static DevelopmentalDecision ParseDecision(string d) => d switch { "APPROVE" => DevelopmentalDecision.Approve, "REJECT" => DevelopmentalDecision.Reject, "RETURN_TO_REPAIR" => DevelopmentalDecision.ReturnToRepair, _ => throw new InvalidOperationException(d) };
    private static string DbSeverity(DevelopmentalSeverity s) => s.ToString().ToUpperInvariant();
    private static DevelopmentalSeverity ParseSeverity(string s) => Enum.Parse<DevelopmentalSeverity>(s, true);
    private static string DbArea(DevelopmentalFindingArea a) => a.ToString().ToUpperInvariant();
    private static DevelopmentalFindingArea ParseArea(string a) => Enum.Parse<DevelopmentalFindingArea>(a, true);
    private static Guid MessageId(Guid request) => new(SHA256.HashData(request.ToByteArray()).AsSpan(0, 16));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Text(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

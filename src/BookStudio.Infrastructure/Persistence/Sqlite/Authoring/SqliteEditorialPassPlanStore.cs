using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteEditorialPassPlanStore : IEditorialPassPlanStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    private static readonly EditorialPassKind[] Order =
    [
        EditorialPassKind.Developmental,
        EditorialPassKind.StructuralContent,
        EditorialPassKind.VoiceLine,
        EditorialPassKind.Dialogue,
        EditorialPassKind.ThemesPacing,
        EditorialPassKind.CopyeditProofreading,
        EditorialPassKind.BetaReaders,
        EditorialPassKind.OriginalityReadAloud
    ];

    public SqliteEditorialPassPlanStore(SqliteConnectionFactory factory, int capacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, capacity);
    }

    public ValueTask<EditorialPassPlanCreateResult> CreateAsync(EditorialPassPlanDraft d, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(d);
        var hash = Hash(JsonSerializer.Serialize(new { d.ProjectId, d.WorkspaceId, d.CrossChapterAuditId, d.ExpectedAuditRevision, d.ExpectedAuditDigest, d.Version, d.Actor }));
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, d.WorkspaceId, d.PlanId);
            if (existing is not null)
            {
                var receipt = ReadReceipt(c, tx, d.WorkspaceId, d.PlanId) ?? throw new EditorialPassConflictException("Create receipt missing.");
                RequireReceipt(receipt, "CREATE", d.WorkspaceId, d.PlanId, d.RequestFingerprint, hash);
                return new EditorialPassPlanCreateResult(existing, true);
            }
            RequireAuthority(c, tx, d.WorkspaceId, d.ProjectId, d.CrossChapterAuditId, d.ExpectedAuditRevision, d.ExpectedAuditDigest);
            Execute(c, tx, "INSERT INTO editorial_pass_plans(workspace_id,plan_id,project_id,cross_chapter_audit_id,expected_audit_revision,expected_audit_digest,version,actor,revision,status,message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$a,$r,$d,$v,$actor,1,'PLANNED',$m,$at,$at);",
                ("$w", d.WorkspaceId), ("$id", d.PlanId.ToString("D")), ("$p", d.ProjectId.ToString("D")), ("$a", d.CrossChapterAuditId.ToString("D")), ("$r", d.ExpectedAuditRevision), ("$d", d.ExpectedAuditDigest), ("$v", d.Version), ("$actor", d.Actor), ("$m", MessageId(d.PlanId).ToString("D")), ("$at", Text(at)));
            for (var i = 0; i < Order.Length; i++)
            {
                var deps = i == 0 ? Array.Empty<EditorialPassKind>() : new[] { Order[i - 1] };
                Execute(c, tx, "INSERT INTO editorial_pass_nodes(workspace_id,plan_id,pass_kind,ordinal,dependencies_json,status,attempts,gate_result,evidence,result,responsible,started_at_utc,completed_at_utc) VALUES($w,$id,$k,$o,$deps,$s,0,NULL,NULL,NULL,NULL,NULL,NULL);",
                    ("$w", d.WorkspaceId), ("$id", d.PlanId.ToString("D")), ("$k", DbPass(Order[i])), ("$o", i), ("$deps", JsonSerializer.Serialize(deps)), ("$s", i == 0 ? "READY" : "PENDING"));
            }
            InsertHistory(c, tx, d.WorkspaceId, d.PlanId, 1, "CREATE", null, d.Actor, null, new { d.ProjectId, d.CrossChapterAuditId, d.ExpectedAuditRevision, d.ExpectedAuditDigest }, at);
            InsertReceipt(c, tx, d.WorkspaceId, d.PlanId, d.PlanId, "CREATE", d.RequestFingerprint, hash, 1, MessageId(d.PlanId), at);
            InsertOutbox(c, tx, MessageId(d.PlanId), "editorial.pass-plan.planned", new { d.WorkspaceId, d.PlanId, d.ProjectId, d.CrossChapterAuditId, d.Actor }, at);
            return new EditorialPassPlanCreateResult(Require(c, tx, d.WorkspaceId, d.PlanId), false);
        }, ct);
    }

    public ValueTask<EditorialPassPlan> StartPassAsync(EditorialPassCommand cmd, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.PlanId, "START", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.PlanId, cmd.ExpectedRevision, cmd.Pass, cmd.Actor })), cmd.ExpectedRevision, at, (c, tx, plan) =>
        {
            if (!AuthorityMatches(c, tx, plan)) return MarkStaleCore(c, tx, plan, cmd.RequestId, "Authority drift.", cmd.Actor, at);
            if (plan.Status is EditorialPlanStatus.Completed or EditorialPlanStatus.Stale) throw new EditorialPassTransitionException("Terminal plan cannot start a pass.");
            var node = Node(plan, cmd.Pass);
            if (node.Status != EditorialPassStatus.Ready) throw new EditorialPassTransitionException("Pass is not ready.");
            if (node.Dependencies.Any(d => Node(plan, d).Status != EditorialPassStatus.Completed || Node(plan, d).Gate != EditorialGateResult.Pass)) throw new EditorialPassTransitionException("Dependencies are not green.");
            var revision = plan.Revision + 1;
            Execute(c, tx, "UPDATE editorial_pass_nodes SET status='IN_PROGRESS',attempts=attempts+1,responsible=$a,started_at_utc=$at WHERE workspace_id=$w AND plan_id=$id AND pass_kind=$k;", ("$a", cmd.Actor), ("$at", Text(at)), ("$w", plan.WorkspaceId), ("$id", plan.PlanId.ToString("D")), ("$k", DbPass(cmd.Pass)));
            UpdatePlan(c, tx, plan, EditorialPlanStatus.InProgress, revision, MessageId(cmd.RequestId), at);
            InsertHistory(c, tx, plan.WorkspaceId, plan.PlanId, revision, "START", cmd.Pass, cmd.Actor, null, new { cmd.Pass }, at);
            InsertOutbox(c, tx, MessageId(cmd.RequestId), "editorial.pass.started", new { plan.WorkspaceId, plan.PlanId, cmd.Pass, cmd.Actor }, at);
            return Require(c, tx, plan.WorkspaceId, plan.PlanId);
        }, ct);

    public ValueTask<EditorialPassPlan> RecordGateAsync(EditorialPassGateCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Evidence)) throw new EditorialPassValidationException("Gate evidence is required.");
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.PlanId, "GATE", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.PlanId, cmd.ExpectedRevision, cmd.Pass, cmd.Result, cmd.Evidence, cmd.Actor })), cmd.ExpectedRevision, at, (c, tx, plan) =>
        {
            if (!AuthorityMatches(c, tx, plan)) return MarkStaleCore(c, tx, plan, cmd.RequestId, "Authority drift.", cmd.Actor, at);
            var node = Node(plan, cmd.Pass);
            if (node.Status != EditorialPassStatus.InProgress) throw new EditorialPassTransitionException("Only an in-progress pass can record a gate.");
            var revision = plan.Revision + 1;
            var message = MessageId(cmd.RequestId);
            Execute(c, tx, "UPDATE editorial_pass_nodes SET gate_result=$g,evidence=$e,status=$s WHERE workspace_id=$w AND plan_id=$id AND pass_kind=$k;", ("$g", cmd.Result == EditorialGateResult.Pass ? "PASS" : "FAIL"), ("$e", cmd.Evidence), ("$s", cmd.Result == EditorialGateResult.Pass ? "IN_PROGRESS" : "BLOCKED"), ("$w", plan.WorkspaceId), ("$id", plan.PlanId.ToString("D")), ("$k", DbPass(cmd.Pass)));
            UpdatePlan(c, tx, plan, cmd.Result == EditorialGateResult.Pass ? EditorialPlanStatus.InProgress : EditorialPlanStatus.Blocked, revision, message, at);
            InsertHistory(c, tx, plan.WorkspaceId, plan.PlanId, revision, "GATE", cmd.Pass, cmd.Actor, cmd.Result == EditorialGateResult.Fail ? "Gate failed." : null, new { cmd.Pass, cmd.Result, cmd.Evidence }, at);
            InsertOutbox(c, tx, message, cmd.Result == EditorialGateResult.Pass ? "editorial.pass.gate-passed" : "editorial.pass.gate-failed", new { plan.WorkspaceId, plan.PlanId, cmd.Pass, cmd.Result, cmd.Actor }, at);
            return Require(c, tx, plan.WorkspaceId, plan.PlanId);
        }, ct);
    }

    public ValueTask<EditorialPassPlan> CompletePassAsync(EditorialPassCompleteCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Result) || string.IsNullOrWhiteSpace(cmd.Evidence)) throw new EditorialPassValidationException("Completion result and evidence are required.");
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.PlanId, "COMPLETE", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.PlanId, cmd.ExpectedRevision, cmd.Pass, cmd.Result, cmd.Evidence, cmd.Actor })), cmd.ExpectedRevision, at, (c, tx, plan) =>
        {
            if (!AuthorityMatches(c, tx, plan)) return MarkStaleCore(c, tx, plan, cmd.RequestId, "Authority drift.", cmd.Actor, at);
            var node = Node(plan, cmd.Pass);
            if (node.Status != EditorialPassStatus.InProgress || node.Gate != EditorialGateResult.Pass) throw new EditorialPassTransitionException("Pass requires a green gate before completion.");
            var revision = plan.Revision + 1;
            var message = MessageId(cmd.RequestId);
            Execute(c, tx, "UPDATE editorial_pass_nodes SET status='COMPLETED',result=$r,evidence=$e,completed_at_utc=$at WHERE workspace_id=$w AND plan_id=$id AND pass_kind=$k;", ("$r", cmd.Result), ("$e", cmd.Evidence), ("$at", Text(at)), ("$w", plan.WorkspaceId), ("$id", plan.PlanId.ToString("D")), ("$k", DbPass(cmd.Pass)));
            var index = Array.IndexOf(Order, cmd.Pass);
            if (index + 1 < Order.Length) Execute(c, tx, "UPDATE editorial_pass_nodes SET status='READY' WHERE workspace_id=$w AND plan_id=$id AND pass_kind=$k AND status='PENDING';", ("$w", plan.WorkspaceId), ("$id", plan.PlanId.ToString("D")), ("$k", DbPass(Order[index + 1])));
            var completed = index == Order.Length - 1;
            UpdatePlan(c, tx, plan, completed ? EditorialPlanStatus.Completed : EditorialPlanStatus.InProgress, revision, message, at);
            InsertHistory(c, tx, plan.WorkspaceId, plan.PlanId, revision, "COMPLETE", cmd.Pass, cmd.Actor, null, new { cmd.Pass, cmd.Result, cmd.Evidence }, at);
            InsertOutbox(c, tx, message, completed ? "editorial.pass-plan.completed" : "editorial.pass.completed", new { plan.WorkspaceId, plan.PlanId, cmd.Pass, cmd.Actor }, at);
            return Require(c, tx, plan.WorkspaceId, plan.PlanId);
        }, ct);
    }

    public ValueTask<EditorialPassPlan> BlockPassAsync(EditorialPassBlockCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new EditorialPassValidationException("Block reason is required.");
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.PlanId, "BLOCK", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.PlanId, cmd.ExpectedRevision, cmd.Pass, cmd.Reason, cmd.Actor })), cmd.ExpectedRevision, at, (c, tx, plan) =>
        {
            var node = Node(plan, cmd.Pass);
            if (node.Status is EditorialPassStatus.Completed or EditorialPassStatus.Pending) throw new EditorialPassTransitionException("Pass cannot be blocked from its current state.");
            var revision = plan.Revision + 1;
            var message = MessageId(cmd.RequestId);
            Execute(c, tx, "UPDATE editorial_pass_nodes SET status='BLOCKED',evidence=$r WHERE workspace_id=$w AND plan_id=$id AND pass_kind=$k;", ("$r", cmd.Reason), ("$w", plan.WorkspaceId), ("$id", plan.PlanId.ToString("D")), ("$k", DbPass(cmd.Pass)));
            UpdatePlan(c, tx, plan, EditorialPlanStatus.Blocked, revision, message, at);
            InsertHistory(c, tx, plan.WorkspaceId, plan.PlanId, revision, "BLOCK", cmd.Pass, cmd.Actor, cmd.Reason, new { cmd.Pass, cmd.Reason }, at);
            InsertOutbox(c, tx, message, "editorial.pass.blocked", new { plan.WorkspaceId, plan.PlanId, cmd.Pass, cmd.Reason, cmd.Actor }, at);
            return Require(c, tx, plan.WorkspaceId, plan.PlanId);
        }, ct);
    }

    public ValueTask<EditorialPassPlan> MarkStaleAsync(EditorialPassStaleCommand cmd, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason)) throw new EditorialPassValidationException("Stale reason is required.");
        return Mutate(cmd.RequestId, cmd.WorkspaceId, cmd.PlanId, "STALE", cmd.RequestFingerprint, Hash(JsonSerializer.Serialize(new { cmd.WorkspaceId, cmd.PlanId, cmd.ExpectedRevision, cmd.Reason, cmd.Actor })), cmd.ExpectedRevision, at, (c, tx, plan) => MarkStaleCore(c, tx, plan, cmd.RequestId, cmd.Reason, cmd.Actor, at), ct);
    }

    public async ValueTask<EditorialPassPlan?> GetAsync(string workspaceId, Guid planId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var c = _factory.OpenConnection();
        return await Task.FromResult(Read(c, null, workspaceId, planId));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false);
    }

    private ValueTask<EditorialPassPlan> Mutate(Guid requestId, string workspace, Guid planId, string action, string fingerprint, string hash, long expectedRevision, DateTimeOffset at, Func<SqliteConnection, SqliteTransaction, EditorialPassPlan, EditorialPassPlan> mutate, CancellationToken ct) =>
        _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var receipt = ReadReceipt(c, tx, workspace, requestId);
            if (receipt is not null)
            {
                RequireReceipt(receipt, action, workspace, planId, fingerprint, hash);
                return Require(c, tx, workspace, planId);
            }
            var plan = Require(c, tx, workspace, planId);
            if (plan.Revision != expectedRevision) throw new EditorialPassConflictException("Stale revision.");
            var result = mutate(c, tx, plan);
            InsertReceipt(c, tx, workspace, requestId, planId, action, fingerprint, hash, result.Revision, result.MessageId, at);
            return result;
        }, ct);

    private static EditorialPassPlan MarkStaleCore(SqliteConnection c, SqliteTransaction tx, EditorialPassPlan plan, Guid requestId, string reason, string actor, DateTimeOffset at)
    {
        if (plan.Status == EditorialPlanStatus.Completed) throw new EditorialPassTransitionException("Completed plan cannot become stale.");
        var revision = plan.Revision + 1;
        var message = MessageId(requestId);
        UpdatePlan(c, tx, plan, EditorialPlanStatus.Stale, revision, message, at);
        InsertHistory(c, tx, plan.WorkspaceId, plan.PlanId, revision, "STALE", null, actor, reason, new { reason }, at);
        InsertOutbox(c, tx, message, "editorial.pass-plan.stale", new { plan.WorkspaceId, plan.PlanId, plan.ProjectId, Reason = reason, Actor = actor }, at);
        return Require(c, tx, plan.WorkspaceId, plan.PlanId);
    }

    private static void ValidateDraft(EditorialPassPlanDraft d)
    {
        if (d.PlanId == Guid.Empty || d.ProjectId == Guid.Empty || d.CrossChapterAuditId == Guid.Empty || d.ExpectedAuditRevision < 1 || d.Version < 1 || string.IsNullOrWhiteSpace(d.WorkspaceId) || string.IsNullOrWhiteSpace(d.ExpectedAuditDigest) || string.IsNullOrWhiteSpace(d.Actor) || string.IsNullOrWhiteSpace(d.RequestFingerprint)) throw new EditorialPassValidationException("Complete editorial pass plan is required.");
    }

    private static void RequireAuthority(SqliteConnection c, SqliteTransaction tx, string workspace, Guid project, Guid audit, long revision, string digest)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM cross_chapter_audits WHERE workspace_id=$w AND project_id=$p AND audit_id=$a AND revision=$r AND payload_hash=$d AND status='APPROVED';";
        cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$p", project.ToString("D")); cmd.Parameters.AddWithValue("$a", audit.ToString("D")); cmd.Parameters.AddWithValue("$r", revision); cmd.Parameters.AddWithValue("$d", digest);
        if (cmd.ExecuteScalar() is null) throw new EditorialPassValidationException("Exact approved cross chapter audit was not found.");
    }

    private static bool AuthorityMatches(SqliteConnection c, SqliteTransaction tx, EditorialPassPlan p)
    {
        try { RequireAuthority(c, tx, p.WorkspaceId, p.ProjectId, p.CrossChapterAuditId, p.ExpectedAuditRevision, p.ExpectedAuditDigest); return true; }
        catch (EditorialPassValidationException) { return false; }
    }

    private static EditorialPassNode Node(EditorialPassPlan p, EditorialPassKind kind) => p.Passes.Single(x => x.Pass == kind);

    private sealed record Receipt(string Workspace, Guid PlanId, string Action, string Fingerprint, string Hash);

    private static Receipt? ReadReceipt(SqliteConnection c, SqliteTransaction? tx, string workspace, Guid requestId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT workspace_id,plan_id,action,request_fingerprint,payload_hash FROM editorial_pass_receipts WHERE workspace_id=$w AND request_id=$r;";
        cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$r", requestId.ToString("D"));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4)) : null;
    }

    private static void RequireReceipt(Receipt r, string action, string workspace, Guid plan, string fingerprint, string hash)
    {
        if (r.Workspace != workspace || r.PlanId != plan || r.Action != action || r.Fingerprint != fingerprint || r.Hash != hash) throw new EditorialPassConflictException("Request id reused with different payload.");
    }

    private static EditorialPassPlan Require(SqliteConnection c, SqliteTransaction? tx, string workspace, Guid planId) => Read(c, tx, workspace, planId) ?? throw new EditorialPassValidationException("Editorial pass plan not found.");

    private static EditorialPassPlan? Read(SqliteConnection c, SqliteTransaction? tx, string workspace, Guid planId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT project_id,cross_chapter_audit_id,expected_audit_revision,expected_audit_digest,version,actor,revision,status,message_id,created_at_utc,updated_at_utc FROM editorial_pass_plans WHERE workspace_id=$w AND plan_id=$id;";
        cmd.Parameters.AddWithValue("$w", workspace); cmd.Parameters.AddWithValue("$id", planId.ToString("D"));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var project = Guid.Parse(r.GetString(0)); var audit = Guid.Parse(r.GetString(1)); var authorityRevision = r.GetInt64(2); var digest = r.GetString(3); var version = r.GetInt32(4); var actor = r.GetString(5); var revision = r.GetInt64(6); var status = ParsePlan(r.GetString(7)); Guid? message = r.IsDBNull(8) ? null : Guid.Parse(r.GetString(8)); var created = DateTimeOffset.Parse(r.GetString(9), CultureInfo.InvariantCulture); var updated = DateTimeOffset.Parse(r.GetString(10), CultureInfo.InvariantCulture);
        r.Close();
        var nodes = new List<EditorialPassNode>();
        using var n = c.CreateCommand(); n.Transaction = tx;
        n.CommandText = "SELECT pass_kind,dependencies_json,status,attempts,gate_result,evidence,result,responsible,started_at_utc,completed_at_utc FROM editorial_pass_nodes WHERE workspace_id=$w AND plan_id=$id ORDER BY ordinal;";
        n.Parameters.AddWithValue("$w", workspace); n.Parameters.AddWithValue("$id", planId.ToString("D"));
        using var nr = n.ExecuteReader();
        while (nr.Read()) nodes.Add(new(ParsePass(nr.GetString(0)), JsonSerializer.Deserialize<List<EditorialPassKind>>(nr.GetString(1)) ?? [], ParsePassStatus(nr.GetString(2)), nr.GetInt32(3), nr.IsDBNull(4) ? null : ParseGate(nr.GetString(4)), nr.IsDBNull(5) ? null : nr.GetString(5), nr.IsDBNull(6) ? null : nr.GetString(6), nr.IsDBNull(7) ? null : nr.GetString(7), nr.IsDBNull(8) ? null : DateTimeOffset.Parse(nr.GetString(8), CultureInfo.InvariantCulture), nr.IsDBNull(9) ? null : DateTimeOffset.Parse(nr.GetString(9), CultureInfo.InvariantCulture)));
        return new(planId, project, workspace, audit, authorityRevision, digest, version, actor, revision, status, nodes, message, created, updated);
    }

    private static void UpdatePlan(SqliteConnection c, SqliteTransaction tx, EditorialPassPlan p, EditorialPlanStatus status, long revision, Guid message, DateTimeOffset at) =>
        Execute(c, tx, "UPDATE editorial_pass_plans SET status=$s,revision=$r,message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND plan_id=$id;", ("$s", DbPlan(status)), ("$r", revision), ("$m", message.ToString("D")), ("$at", Text(at)), ("$w", p.WorkspaceId), ("$id", p.PlanId.ToString("D")));

    private static void InsertHistory(SqliteConnection c, SqliteTransaction tx, string workspace, Guid plan, long revision, string action, EditorialPassKind? pass, string actor, string? reason, object payload, DateTimeOffset at) =>
        Execute(c, tx, "INSERT INTO editorial_pass_history(workspace_id,plan_id,revision,action,pass_kind,actor,reason,payload_json,occurred_at_utc) VALUES($w,$p,$r,$a,$k,$actor,$reason,$j,$at);", ("$w", workspace), ("$p", plan.ToString("D")), ("$r", revision), ("$a", action), ("$k", pass is null ? DBNull.Value : DbPass(pass.Value)), ("$actor", actor), ("$reason", reason is null ? DBNull.Value : reason), ("$j", JsonSerializer.Serialize(payload)), ("$at", Text(at)));

    private static void InsertReceipt(SqliteConnection c, SqliteTransaction tx, string workspace, Guid request, Guid plan, string action, string fingerprint, string hash, long revision, Guid? message, DateTimeOffset at) =>
        Execute(c, tx, "INSERT INTO editorial_pass_receipts(workspace_id,request_id,plan_id,action,request_fingerprint,payload_hash,resulting_revision,message_id,created_at_utc) VALUES($w,$r,$p,$a,$f,$h,$v,$m,$at);", ("$w", workspace), ("$r", request.ToString("D")), ("$p", plan.ToString("D")), ("$a", action), ("$f", fingerprint), ("$h", hash), ("$v", revision), ("$m", message is null ? DBNull.Value : message.Value.ToString("D")), ("$at", Text(at)));

    private static void InsertOutbox(SqliteConnection c, SqliteTransaction tx, Guid message, string type, object payload, DateTimeOffset at) =>
        Execute(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($id,$t,'1.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);", ("$id", message.ToString("D")), ("$t", type), ("$p", JsonSerializer.Serialize(payload)), ("$at", Text(at)));

    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] values)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static string DbPlan(EditorialPlanStatus s) => s switch { EditorialPlanStatus.Planned => "PLANNED", EditorialPlanStatus.InProgress => "IN_PROGRESS", EditorialPlanStatus.Blocked => "BLOCKED", EditorialPlanStatus.Completed => "COMPLETED", EditorialPlanStatus.Stale => "STALE", _ => throw new ArgumentOutOfRangeException(nameof(s)) };
    private static EditorialPlanStatus ParsePlan(string s) => s switch { "PLANNED" => EditorialPlanStatus.Planned, "IN_PROGRESS" => EditorialPlanStatus.InProgress, "BLOCKED" => EditorialPlanStatus.Blocked, "COMPLETED" => EditorialPlanStatus.Completed, "STALE" => EditorialPlanStatus.Stale, _ => throw new InvalidOperationException(s) };
    private static string DbPass(EditorialPassKind p) => p.ToString().ToUpperInvariant();
    private static EditorialPassKind ParsePass(string p) => Enum.Parse<EditorialPassKind>(p, true);
    private static EditorialPassStatus ParsePassStatus(string s) => s switch { "PENDING" => EditorialPassStatus.Pending, "READY" => EditorialPassStatus.Ready, "IN_PROGRESS" => EditorialPassStatus.InProgress, "BLOCKED" => EditorialPassStatus.Blocked, "COMPLETED" => EditorialPassStatus.Completed, _ => throw new InvalidOperationException(s) };
    private static EditorialGateResult ParseGate(string s) => s == "PASS" ? EditorialGateResult.Pass : EditorialGateResult.Fail;
    private static Guid MessageId(Guid request) => new(SHA256.HashData(request.ToByteArray()).AsSpan(0, 16));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Text(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

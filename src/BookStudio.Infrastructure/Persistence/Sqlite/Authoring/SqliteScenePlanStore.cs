using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteScenePlanStore : IScenePlanStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteScenePlanStore(SqliteConnectionFactory factory, int writeQueueCapacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, writeQueueCapacity);
    }

    public ValueTask<ScenePlanCreateResult> CreateAsync(ScenePlanDraft draft, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(draft);
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, draft.WorkspaceId, draft.ScenePlanId);
            if (existing is not null)
            {
                if (existing.ProjectId == draft.ProjectId && existing.BookPlanId == draft.BookPlanId && existing.BookPlanVersion == draft.BookPlanVersion && existing.BookPlanApprovalMessageId == draft.BookPlanApprovalMessageId && existing.BookPlanContentDigest == draft.BookPlanContentDigest && existing.SchemaVersion == draft.SchemaVersion && Same(existing.Current.Content, draft.Content))
                    return new ScenePlanCreateResult(existing, true);
                throw new ScenePlanConflictException("Scene plan identity already exists with different immutable content.");
            }
            var chapterKeys = RequireApprovedBookPlan(c, tx, draft.WorkspaceId, draft.ProjectId, draft.BookPlanId, draft.BookPlanVersion, draft.BookPlanApprovalMessageId, draft.BookPlanContentDigest);
            ValidateContent(draft.Content, chapterKeys);
            Execute(c, tx, "INSERT INTO scene_plans(workspace_id,scene_plan_id,project_id,book_plan_id,book_plan_version,book_plan_approval_message_id,book_plan_content_digest,schema_version,current_version,approval_message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$b,$bv,$bm,$bd,$s,1,NULL,$at,$at);",
                ("$w",draft.WorkspaceId),("$id",draft.ScenePlanId.ToString("D")),("$p",draft.ProjectId.ToString("D")),("$b",draft.BookPlanId.ToString("D")),("$bv",draft.BookPlanVersion),("$bm",draft.BookPlanApprovalMessageId.ToString("D")),("$bd",draft.BookPlanContentDigest),("$s",draft.SchemaVersion),("$at",Text(at)));
            InsertVersion(c, tx, draft.WorkspaceId, draft.ScenePlanId, 1, 1, ScenePlanStatus.Draft, draft.Content, null, draft.Actor, "initial", at);
            InsertRequest(c, tx, DeterministicCreateRequestId(draft), draft.WorkspaceId, draft.ScenePlanId, "CREATE", draft.RequestFingerprint, 1, 1, null, at);
            return new ScenePlanCreateResult(Require(c, tx, draft.WorkspaceId, draft.ScenePlanId), false);
        }, ct);
    }

    public ValueTask<ScenePlan> ReviseAsync(ScenePlanRevisionCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(command.RequestId, command.WorkspaceId, command.ScenePlanId, command.Actor, command.RequestFingerprint);
        if (string.IsNullOrWhiteSpace(command.Reason)) throw new ScenePlanValidationException("Revision reason is required.");
        return Mutate(command.RequestId, command.WorkspaceId, command.ScenePlanId, "REVISE", command.RequestFingerprint, at, (c, tx, current) =>
        {
            RequireExpected(current, command.ExpectedVersion, command.ExpectedRevision);
            if (current.Current.Status != ScenePlanStatus.Draft) throw new ScenePlanTransitionException("Only a draft scene plan can be revised.");
            ValidateAgainstBookPlan(c, tx, current, command.Content);
            InsertVersion(c, tx, command.WorkspaceId, command.ScenePlanId, current.CurrentVersion, current.Current.Revision + 1, ScenePlanStatus.Draft, command.Content, null, command.Actor, command.Reason, at);
        }, ct);
    }

    public ValueTask<ScenePlan> PrepareAsync(ScenePlanControlCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Control(command, "PREPARE", ScenePlanStatus.Draft, ScenePlanStatus.Prepared, at, ct);

    public ValueTask<ScenePlan> CommitAsync(ScenePlanControlCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Control(command, "COMMIT", ScenePlanStatus.Prepared, ScenePlanStatus.Committed, at, ct);

    public ValueTask<ScenePlanApprovalResult> ApproveAsync(ScenePlanApprovalCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(command.RequestId, command.WorkspaceId, command.ScenePlanId, command.Actor, command.RequestFingerprint);
        if (string.IsNullOrWhiteSpace(command.Reason)) throw new ScenePlanValidationException("Approval reason is required.");
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var prior = ReadRequest(c, tx, command.RequestId);
            if (prior is not null)
            {
                RequireRequest(prior, "APPROVE", command.WorkspaceId, command.ScenePlanId, command.RequestFingerprint);
                return new ScenePlanApprovalResult(Require(c, tx, command.WorkspaceId, command.ScenePlanId), true, prior.ApprovalMessageId ?? throw new InvalidOperationException("Approval receipt is missing message identity."));
            }
            var current = Require(c, tx, command.WorkspaceId, command.ScenePlanId);
            RequireExpected(current, command.ExpectedVersion, command.ExpectedRevision);
            if (current.Current.Status != ScenePlanStatus.Committed) throw new ScenePlanTransitionException("Only a committed scene plan can be approved.");
            var messageId = DeterministicMessageId(command.RequestId);
            var nextRevision = current.Current.Revision + 1;
            InsertVersion(c, tx, command.WorkspaceId, command.ScenePlanId, current.CurrentVersion, nextRevision, ScenePlanStatus.Approved, current.Current.Content, current.Current.ContentDigest, command.Actor, command.Reason, at);
            Execute(c, tx, "UPDATE scene_plans SET approval_message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND scene_plan_id=$id;",("$m",messageId.ToString("D")),("$at",Text(at)),("$w",command.WorkspaceId),("$id",command.ScenePlanId.ToString("D")));
            Execute(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($m,'editorial.scene-plan.approved','1.0.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);",
                ("$m",messageId.ToString("D")),("$p",JsonSerializer.Serialize(new { command.WorkspaceId, command.ScenePlanId, current.ProjectId, current.BookPlanId, version=current.CurrentVersion, revision=nextRevision, contentDigest=current.Current.ContentDigest, approvedBy=command.Actor })),("$at",Text(at)));
            InsertRequest(c, tx, command.RequestId, command.WorkspaceId, command.ScenePlanId, "APPROVE", command.RequestFingerprint, current.CurrentVersion, nextRevision, messageId, at);
            return new ScenePlanApprovalResult(Require(c, tx, command.WorkspaceId, command.ScenePlanId), false, messageId);
        }, ct);
    }

    public ValueTask<ScenePlan> OpenNextVersionAsync(ScenePlanNextVersionCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(command.RequestId, command.WorkspaceId, command.ScenePlanId, command.Actor, command.RequestFingerprint);
        if (string.IsNullOrWhiteSpace(command.Reason)) throw new ScenePlanValidationException("New-version reason is required.");
        return Mutate(command.RequestId, command.WorkspaceId, command.ScenePlanId, "NEXT_VERSION", command.RequestFingerprint, at, (c, tx, current) =>
        {
            if (current.CurrentVersion != command.ExpectedVersion) throw new ScenePlanConflictException("Scene plan version conflict.");
            if (current.Current.Status != ScenePlanStatus.Approved) throw new ScenePlanTransitionException("A new version can only be opened from an approved scene plan.");
            ValidateAgainstBookPlan(c, tx, current, command.Content);
            var next = current.CurrentVersion + 1;
            InsertVersion(c, tx, command.WorkspaceId, command.ScenePlanId, next, 1, ScenePlanStatus.Draft, command.Content, null, command.Actor, command.Reason, at);
            Execute(c, tx, "UPDATE scene_plans SET current_version=$v,approval_message_id=NULL,updated_at_utc=$at WHERE workspace_id=$w AND scene_plan_id=$id;",("$v",next),("$at",Text(at)),("$w",command.WorkspaceId),("$id",command.ScenePlanId.ToString("D")));
        }, ct);
    }

    public async ValueTask<ScenePlan?> GetAsync(string workspaceId, Guid scenePlanId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var c = _factory.OpenConnection(); return await Task.FromResult(Read(c, null, workspaceId, scenePlanId)).ConfigureAwait(false); }

    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false); }

    private ValueTask<ScenePlan> Control(ScenePlanControlCommand command, string operation, ScenePlanStatus expected, ScenePlanStatus next, DateTimeOffset at, CancellationToken ct)
    {
        ValidateRequest(command.RequestId, command.WorkspaceId, command.ScenePlanId, command.Actor, command.RequestFingerprint);
        return Mutate(command.RequestId, command.WorkspaceId, command.ScenePlanId, operation, command.RequestFingerprint, at, (c, tx, current) =>
        {
            RequireExpected(current, command.ExpectedVersion, command.ExpectedRevision);
            if (current.Current.Status != expected) throw new ScenePlanTransitionException($"Scene plan must be {expected} before {operation.ToLowerInvariant()}.");
            ValidateAgainstBookPlan(c, tx, current, current.Current.Content);
            var digest = next == ScenePlanStatus.Committed ? Digest(current.Current.Content) : current.Current.ContentDigest;
            InsertVersion(c, tx, command.WorkspaceId, command.ScenePlanId, current.CurrentVersion, current.Current.Revision + 1, next, current.Current.Content, digest, command.Actor, operation.ToLowerInvariant(), at);
        }, ct);
    }

    private ValueTask<ScenePlan> Mutate(Guid requestId, string workspaceId, Guid scenePlanId, string operation, string fingerprint, DateTimeOffset at, Action<SqliteConnection, SqliteTransaction, ScenePlan> action, CancellationToken ct) =>
        _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var prior = ReadRequest(c, tx, requestId);
            if (prior is not null) { RequireRequest(prior, operation, workspaceId, scenePlanId, fingerprint); return Require(c, tx, workspaceId, scenePlanId); }
            var current = Require(c, tx, workspaceId, scenePlanId);
            action(c, tx, current);
            Execute(c, tx, "UPDATE scene_plans SET updated_at_utc=$at WHERE workspace_id=$w AND scene_plan_id=$id;",("$at",Text(at)),("$w",workspaceId),("$id",scenePlanId.ToString("D")));
            var updated = Require(c, tx, workspaceId, scenePlanId);
            InsertRequest(c, tx, requestId, workspaceId, scenePlanId, operation, fingerprint, updated.CurrentVersion, updated.Current.Revision, updated.ApprovalMessageId, at);
            return updated;
        }, ct);

    private sealed record RequestRow(string WorkspaceId, Guid ScenePlanId, string Operation, string Fingerprint, Guid? ApprovalMessageId);

    private static ScenePlan Require(SqliteConnection c, SqliteTransaction tx, string w, Guid id) => Read(c, tx, w, id) ?? throw new KeyNotFoundException("Scene plan was not found.");

    private static ScenePlan? Read(SqliteConnection c, SqliteTransaction? tx, string w, Guid id)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT project_id,book_plan_id,book_plan_version,book_plan_approval_message_id,book_plan_content_digest,schema_version,current_version,approval_message_id,created_at_utc,updated_at_utc FROM scene_plans WHERE workspace_id=$w AND scene_plan_id=$id;";
        cmd.Parameters.AddWithValue("$w", w); cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
        var project = Guid.Parse(r.GetString(0)); var bookPlan = Guid.Parse(r.GetString(1)); var bookPlanVersion = r.GetInt64(2); var bookPlanMessage = Guid.Parse(r.GetString(3)); var bookPlanDigest = r.GetString(4); var schema = r.GetString(5); var currentVersion = r.GetInt64(6); Guid? approval = r.IsDBNull(7) ? null : Guid.Parse(r.GetString(7)); var created = ParseTime(r.GetString(8)); var updated = ParseTime(r.GetString(9)); r.Close();
        using var versions = c.CreateCommand(); versions.Transaction = tx;
        versions.CommandText = "SELECT version,revision,status,content_json,content_digest,actor,reason,created_at_utc,updated_at_utc FROM scene_plan_versions WHERE workspace_id=$w AND scene_plan_id=$id ORDER BY version,revision;";
        versions.Parameters.AddWithValue("$w", w); versions.Parameters.AddWithValue("$id", id.ToString("D"));
        using var vr = versions.ExecuteReader(); var latest = new Dictionary<long, ScenePlanVersion>();
        while (vr.Read()) { var item = new ScenePlanVersion(vr.GetInt64(0), vr.GetInt64(1), Enum.Parse<ScenePlanStatus>(vr.GetString(2), true), JsonSerializer.Deserialize<ScenePlanContent>(vr.GetString(3)) ?? throw new InvalidOperationException("Invalid scene plan content."), vr.IsDBNull(4) ? null : vr.GetString(4), vr.GetString(5), vr.GetString(6), ParseTime(vr.GetString(7)), ParseTime(vr.GetString(8))); latest[item.Version] = item; }
        return new(id, project, bookPlan, bookPlanVersion, bookPlanMessage, bookPlanDigest, w, schema, currentVersion, latest.Values.OrderBy(x => x.Version).ToArray(), approval, created, updated);
    }

    private static RequestRow? ReadRequest(SqliteConnection c, SqliteTransaction tx, Guid id)
    { using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="SELECT workspace_id,scene_plan_id,operation,request_fingerprint,approval_message_id FROM scene_plan_requests WHERE request_id=$id;"; cmd.Parameters.AddWithValue("$id",id.ToString("D")); using var r=cmd.ExecuteReader(); return r.Read()?new(r.GetString(0),Guid.Parse(r.GetString(1)),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:Guid.Parse(r.GetString(4))):null; }

    private static void InsertVersion(SqliteConnection c, SqliteTransaction tx, string w, Guid id, long version, long revision, ScenePlanStatus status, ScenePlanContent content, string? digest, string actor, string reason, DateTimeOffset at) =>
        Execute(c, tx, "INSERT INTO scene_plan_versions(workspace_id,scene_plan_id,version,revision,status,content_json,content_digest,actor,reason,created_at_utc,updated_at_utc) VALUES($w,$id,$v,$r,$s,$c,$d,$a,$reason,$at,$at);",("$w",w),("$id",id.ToString("D")),("$v",version),("$r",revision),("$s",status.ToString().ToUpperInvariant()),("$c",JsonSerializer.Serialize(content)),("$d",digest is null?DBNull.Value:digest),("$a",actor),("$reason",reason),("$at",Text(at)));

    private static void InsertRequest(SqliteConnection c, SqliteTransaction tx, Guid request, string w, Guid id, string op, string fingerprint, long version, long revision, Guid? message, DateTimeOffset at) =>
        Execute(c, tx, "INSERT INTO scene_plan_requests(request_id,workspace_id,scene_plan_id,operation,request_fingerprint,result_version,result_revision,approval_message_id,created_at_utc) VALUES($r,$w,$id,$op,$f,$v,$rev,$m,$at);",("$r",request.ToString("D")),("$w",w),("$id",id.ToString("D")),("$op",op),("$f",fingerprint),("$v",version),("$rev",revision),("$m",message is null?DBNull.Value:message.Value.ToString("D")),("$at",Text(at)));

    private static HashSet<string> RequireApprovedBookPlan(SqliteConnection c, SqliteTransaction tx, string workspaceId, Guid projectId, Guid bookPlanId, long version, Guid approvalMessageId, string expectedDigest)
    {
        using var cmd=c.CreateCommand(); cmd.Transaction=tx;
        cmd.CommandText="SELECT current_version,approval_message_id FROM book_plans WHERE workspace_id=$w AND plan_id=$id AND project_id=$p;";
        cmd.Parameters.AddWithValue("$w",workspaceId); cmd.Parameters.AddWithValue("$id",bookPlanId.ToString("D")); cmd.Parameters.AddWithValue("$p",projectId.ToString("D"));
        using var r=cmd.ExecuteReader(); if(!r.Read()||r.GetInt64(0)!=version||r.IsDBNull(1)||Guid.Parse(r.GetString(1))!=approvalMessageId) throw new ScenePlanValidationException("An approved book plan with matching version and approval evidence is required."); r.Close();
        using var versionCmd=c.CreateCommand(); versionCmd.Transaction=tx;
        versionCmd.CommandText="SELECT content_json,content_digest,status FROM book_plan_versions WHERE workspace_id=$w AND plan_id=$id AND version=$v ORDER BY revision DESC LIMIT 1;";
        versionCmd.Parameters.AddWithValue("$w",workspaceId); versionCmd.Parameters.AddWithValue("$id",bookPlanId.ToString("D")); versionCmd.Parameters.AddWithValue("$v",version);
        using var vr=versionCmd.ExecuteReader(); if(!vr.Read()||!string.Equals(vr.GetString(2),"APPROVED",StringComparison.Ordinal)||vr.IsDBNull(1)||!string.Equals(vr.GetString(1),expectedDigest,StringComparison.Ordinal)) throw new ScenePlanValidationException("Approved book plan digest does not match causal evidence.");
        var content=JsonSerializer.Deserialize<BookPlanContent>(vr.GetString(0))??throw new ScenePlanValidationException("Approved book plan content is invalid.");
        return content.Chapters.Select(x=>x.Key).ToHashSet(StringComparer.Ordinal);
    }

    private static void ValidateAgainstBookPlan(SqliteConnection c, SqliteTransaction tx, ScenePlan current, ScenePlanContent content)
    {
        var chapterKeys=RequireApprovedBookPlan(c,tx,current.WorkspaceId,current.ProjectId,current.BookPlanId,current.BookPlanVersion,current.BookPlanApprovalMessageId,current.BookPlanContentDigest);
        ValidateContent(content,chapterKeys);
    }

    private static void RequireRequest(RequestRow r, string op, string w, Guid id, string fingerprint)
    { if(r.Operation!=op||r.WorkspaceId!=w||r.ScenePlanId!=id||r.Fingerprint!=fingerprint) throw new ScenePlanConflictException("Request ID was reused with different immutable content."); }

    private static void RequireExpected(ScenePlan p, long version, long revision)
    { if(p.CurrentVersion!=version||p.Current.Revision!=revision) throw new ScenePlanConflictException("Expected scene plan version or revision is stale."); }

    private static void ValidateDraft(ScenePlanDraft d)
    { if(d is null||d.ScenePlanId==Guid.Empty||d.ProjectId==Guid.Empty||d.BookPlanId==Guid.Empty||d.BookPlanVersion<=0||d.BookPlanApprovalMessageId==Guid.Empty||string.IsNullOrWhiteSpace(d.BookPlanContentDigest)||string.IsNullOrWhiteSpace(d.WorkspaceId)||string.IsNullOrWhiteSpace(d.SchemaVersion)||string.IsNullOrWhiteSpace(d.Actor)||string.IsNullOrWhiteSpace(d.RequestFingerprint)) throw new ScenePlanValidationException("Scene plan draft is incomplete."); if(d.Content is null) throw new ScenePlanValidationException("Scene plan content is required."); }

    private static void ValidateRequest(Guid requestId,string workspaceId,Guid scenePlanId,string actor,string fingerprint)
    { if(requestId==Guid.Empty||scenePlanId==Guid.Empty||string.IsNullOrWhiteSpace(workspaceId)||string.IsNullOrWhiteSpace(actor)||string.IsNullOrWhiteSpace(fingerprint)) throw new ScenePlanValidationException("Scene plan request is incomplete."); }

    private static void ValidateContent(ScenePlanContent content, IReadOnlySet<string> chapterKeys)
    {
        if(content is null||content.Scenes is null||content.GlobalConstraints is null||content.AcceptanceCriteria is null||content.Scenes.Count==0||content.AcceptanceCriteria.Count==0) throw new ScenePlanValidationException("Scene plan content is incomplete.");
        var keys=new HashSet<string>(StringComparer.Ordinal); var orders=new HashSet<(string,int)>();
        foreach(var scene in content.Scenes)
        {
            if(string.IsNullOrWhiteSpace(scene.Key)||!chapterKeys.Contains(scene.ChapterKey)||scene.Order<=0||string.IsNullOrWhiteSpace(scene.Title)||string.IsNullOrWhiteSpace(scene.Purpose)||string.IsNullOrWhiteSpace(scene.Summary)||scene.Beats is null||scene.Beats.Count==0||scene.RequiredEvidence is null||scene.Constraints is null||scene.AcceptanceCriteria is null||scene.AcceptanceCriteria.Count==0||scene.DependsOn is null||!keys.Add(scene.Key)||!orders.Add((scene.ChapterKey,scene.Order))) throw new ScenePlanValidationException("Scenes require unique keys and chapter-local order with complete outcomes.");
        }
        var covered=content.Scenes.Select(x=>x.ChapterKey).ToHashSet(StringComparer.Ordinal);
        if(!covered.SetEquals(chapterKeys)) throw new ScenePlanValidationException("Every approved book-plan chapter must have at least one scene.");
        foreach(var scene in content.Scenes) foreach(var dependency in scene.DependsOn) if(dependency==scene.Key||!keys.Contains(dependency)) throw new ScenePlanValidationException("Scene dependency is missing or self-referential.");
        var map=content.Scenes.ToDictionary(x=>x.Key,x=>x.DependsOn,StringComparer.Ordinal); var visiting=new HashSet<string>(StringComparer.Ordinal); var visited=new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string key){ if(visited.Contains(key)) return false; if(!visiting.Add(key)) return true; foreach(var dep in map[key]) if(Visit(dep)) return true; visiting.Remove(key); visited.Add(key); return false; }
        if(map.Keys.Any(Visit)) throw new ScenePlanValidationException("Scene dependencies must be acyclic.");
    }

    private static bool Same(ScenePlanContent a,ScenePlanContent b)=>JsonSerializer.Serialize(a)==JsonSerializer.Serialize(b);
    private static string Digest(ScenePlanContent content)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content)))).ToLowerInvariant();
    private static Guid DeterministicMessageId(Guid requestId)=>new(SHA256.HashData(Encoding.UTF8.GetBytes("scene-plan-approval:"+requestId.ToString("D")))[..16]);
    private static Guid DeterministicCreateRequestId(ScenePlanDraft draft)=>new(SHA256.HashData(Encoding.UTF8.GetBytes("scene-plan-create:"+draft.WorkspaceId+":"+draft.ScenePlanId.ToString("D")))[..16]);
    private static string Text(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string value)=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
    private static void Execute(SqliteConnection c,SqliteTransaction tx,string sql,params (string Name,object Value)[] values){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var item in values)cmd.Parameters.AddWithValue(item.Name,item.Value);cmd.ExecuteNonQuery();}
}

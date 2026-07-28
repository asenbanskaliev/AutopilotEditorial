using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteBookPlanStore : IBookPlanStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteBookPlanStore(SqliteConnectionFactory factory, int writeQueueCapacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, writeQueueCapacity);
    }

    public ValueTask<BookPlanCreateResult> CreateAsync(BookPlanDraft draft, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(draft);
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, draft.WorkspaceId, draft.PlanId);
            if (existing is not null)
            {
                if (existing.ProjectId == draft.ProjectId && existing.SpecificationId == draft.SpecificationId && existing.SpecificationVersion == draft.SpecificationVersion && existing.SpecificationApprovalMessageId == draft.SpecificationApprovalMessageId && existing.SchemaVersion == draft.SchemaVersion && Same(existing.Current.Content, draft.Content))
                    return new BookPlanCreateResult(existing, true);
                throw new BookPlanConflictException("Book plan identity already exists with different immutable content.");
            }
            RequireApprovedSpecification(c, tx, draft);
            Execute(c, tx, "INSERT INTO book_plans(workspace_id,plan_id,project_id,specification_id,specification_version,specification_approval_message_id,schema_version,current_version,approval_message_id,created_at_utc,updated_at_utc) VALUES($w,$id,$p,$s,$sv,$sm,$schema,1,NULL,$at,$at);",
                ("$w",draft.WorkspaceId),("$id",draft.PlanId.ToString("D")),("$p",draft.ProjectId.ToString("D")),("$s",draft.SpecificationId.ToString("D")),("$sv",draft.SpecificationVersion),("$sm",draft.SpecificationApprovalMessageId.ToString("D")),("$schema",draft.SchemaVersion),("$at",Text(at)));
            InsertVersion(c, tx, draft.WorkspaceId, draft.PlanId, 1, 1, BookPlanStatus.Draft, draft.Content, null, draft.Actor, "initial", at);
            InsertRequest(c, tx, DeterministicCreateRequestId(draft), draft.WorkspaceId, draft.PlanId, "CREATE", draft.RequestFingerprint, 1, 1, null, at);
            return new BookPlanCreateResult(Require(c, tx, draft.WorkspaceId, draft.PlanId), false);
        }, ct);
    }

    public ValueTask<BookPlan> ReviseAsync(BookPlanRevisionCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(command.RequestId, command.WorkspaceId, command.PlanId, command.Actor, command.RequestFingerprint);
        ValidateContent(command.Content);
        if (string.IsNullOrWhiteSpace(command.Reason)) throw new BookPlanValidationException("Revision reason is required.");
        return Mutate(command.RequestId, command.WorkspaceId, command.PlanId, "REVISE", command.RequestFingerprint, at, (c, tx, current) =>
        {
            RequireExpected(current, command.ExpectedVersion, command.ExpectedRevision);
            if (current.Current.Status != BookPlanStatus.Draft) throw new BookPlanTransitionException("Only a draft book plan can be revised.");
            InsertVersion(c, tx, command.WorkspaceId, command.PlanId, current.CurrentVersion, current.Current.Revision + 1, BookPlanStatus.Draft, command.Content, null, command.Actor, command.Reason, at);
        }, ct);
    }

    public ValueTask<BookPlan> PrepareAsync(BookPlanControlCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Control(command, "PREPARE", BookPlanStatus.Draft, BookPlanStatus.Prepared, at, ct);

    public ValueTask<BookPlan> CommitAsync(BookPlanControlCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Control(command, "COMMIT", BookPlanStatus.Prepared, BookPlanStatus.Committed, at, ct);

    public ValueTask<BookPlanApprovalResult> ApproveAsync(BookPlanApprovalCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(command.RequestId, command.WorkspaceId, command.PlanId, command.Actor, command.RequestFingerprint);
        if (string.IsNullOrWhiteSpace(command.Reason)) throw new BookPlanValidationException("Approval reason is required.");
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var prior = ReadRequest(c, tx, command.RequestId);
            if (prior is not null)
            {
                RequireRequest(prior, "APPROVE", command.WorkspaceId, command.PlanId, command.RequestFingerprint);
                return new BookPlanApprovalResult(Require(c, tx, command.WorkspaceId, command.PlanId), true, prior.ApprovalMessageId ?? throw new InvalidOperationException("Approval receipt is missing message identity."));
            }
            var current = Require(c, tx, command.WorkspaceId, command.PlanId);
            RequireExpected(current, command.ExpectedVersion, command.ExpectedRevision);
            if (current.Current.Status != BookPlanStatus.Committed) throw new BookPlanTransitionException("Only a committed book plan can be approved.");
            var messageId = DeterministicMessageId(command.RequestId);
            var nextRevision = current.Current.Revision + 1;
            InsertVersion(c, tx, command.WorkspaceId, command.PlanId, current.CurrentVersion, nextRevision, BookPlanStatus.Approved, current.Current.Content, current.Current.ContentDigest, command.Actor, command.Reason, at);
            Execute(c, tx, "UPDATE book_plans SET approval_message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND plan_id=$id;",("$m",messageId.ToString("D")),("$at",Text(at)),("$w",command.WorkspaceId),("$id",command.PlanId.ToString("D")));
            Execute(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($m,'editorial.book-plan.approved','1.0.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);",
                ("$m",messageId.ToString("D")),("$p",JsonSerializer.Serialize(new { command.WorkspaceId, command.PlanId, current.ProjectId, current.SpecificationId, version=current.CurrentVersion, revision=nextRevision, contentDigest=current.Current.ContentDigest, approvedBy=command.Actor })),("$at",Text(at)));
            InsertRequest(c, tx, command.RequestId, command.WorkspaceId, command.PlanId, "APPROVE", command.RequestFingerprint, current.CurrentVersion, nextRevision, messageId, at);
            return new BookPlanApprovalResult(Require(c, tx, command.WorkspaceId, command.PlanId), false, messageId);
        }, ct);
    }

    public ValueTask<BookPlan> OpenNextVersionAsync(BookPlanNextVersionCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(command.RequestId, command.WorkspaceId, command.PlanId, command.Actor, command.RequestFingerprint);
        ValidateContent(command.Content);
        if (string.IsNullOrWhiteSpace(command.Reason)) throw new BookPlanValidationException("New-version reason is required.");
        return Mutate(command.RequestId, command.WorkspaceId, command.PlanId, "NEXT_VERSION", command.RequestFingerprint, at, (c, tx, current) =>
        {
            if (current.CurrentVersion != command.ExpectedVersion) throw new BookPlanConflictException("Book plan version conflict.");
            if (current.Current.Status != BookPlanStatus.Approved) throw new BookPlanTransitionException("A new version can only be opened from an approved plan.");
            var next = current.CurrentVersion + 1;
            InsertVersion(c, tx, command.WorkspaceId, command.PlanId, next, 1, BookPlanStatus.Draft, command.Content, null, command.Actor, command.Reason, at);
            Execute(c, tx, "UPDATE book_plans SET current_version=$v,approval_message_id=NULL,updated_at_utc=$at WHERE workspace_id=$w AND plan_id=$id;",("$v",next),("$at",Text(at)),("$w",command.WorkspaceId),("$id",command.PlanId.ToString("D")));
        }, ct);
    }

    public async ValueTask<BookPlan?> GetAsync(string workspaceId, Guid planId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); using var c = _factory.OpenConnection(); return await Task.FromResult(Read(c, null, workspaceId, planId)).ConfigureAwait(false); }

    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false); }

    private ValueTask<BookPlan> Control(BookPlanControlCommand command, string operation, BookPlanStatus expected, BookPlanStatus next, DateTimeOffset at, CancellationToken ct)
    {
        ValidateRequest(command.RequestId, command.WorkspaceId, command.PlanId, command.Actor, command.RequestFingerprint);
        return Mutate(command.RequestId, command.WorkspaceId, command.PlanId, operation, command.RequestFingerprint, at, (c, tx, current) =>
        {
            RequireExpected(current, command.ExpectedVersion, command.ExpectedRevision);
            if (current.Current.Status != expected) throw new BookPlanTransitionException($"Book plan must be {expected} before {operation.ToLowerInvariant()}.");
            ValidateContent(current.Current.Content);
            var digest = next == BookPlanStatus.Committed ? Digest(current.Current.Content) : current.Current.ContentDigest;
            InsertVersion(c, tx, command.WorkspaceId, command.PlanId, current.CurrentVersion, current.Current.Revision + 1, next, current.Current.Content, digest, command.Actor, operation.ToLowerInvariant(), at);
        }, ct);
    }

    private ValueTask<BookPlan> Mutate(Guid requestId, string workspaceId, Guid planId, string operation, string fingerprint, DateTimeOffset at, Action<SqliteConnection, SqliteTransaction, BookPlan> action, CancellationToken ct) =>
        _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var prior = ReadRequest(c, tx, requestId);
            if (prior is not null) { RequireRequest(prior, operation, workspaceId, planId, fingerprint); return Require(c, tx, workspaceId, planId); }
            var current = Require(c, tx, workspaceId, planId);
            action(c, tx, current);
            Execute(c, tx, "UPDATE book_plans SET updated_at_utc=$at WHERE workspace_id=$w AND plan_id=$id;",("$at",Text(at)),("$w",workspaceId),("$id",planId.ToString("D")));
            var updated = Require(c, tx, workspaceId, planId);
            InsertRequest(c, tx, requestId, workspaceId, planId, operation, fingerprint, updated.CurrentVersion, updated.Current.Revision, updated.ApprovalMessageId, at);
            return updated;
        }, ct);

    private sealed record RequestRow(string WorkspaceId, Guid PlanId, string Operation, string Fingerprint, Guid? ApprovalMessageId);

    private static BookPlan Require(SqliteConnection c, SqliteTransaction tx, string w, Guid id) => Read(c, tx, w, id) ?? throw new KeyNotFoundException("Book plan was not found.");

    private static BookPlan? Read(SqliteConnection c, SqliteTransaction? tx, string w, Guid id)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT project_id,specification_id,specification_version,specification_approval_message_id,schema_version,current_version,approval_message_id,created_at_utc,updated_at_utc FROM book_plans WHERE workspace_id=$w AND plan_id=$id;";
        cmd.Parameters.AddWithValue("$w", w); cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
        var project = Guid.Parse(r.GetString(0)); var specification = Guid.Parse(r.GetString(1)); var specificationVersion = r.GetInt64(2); var specificationMessage = Guid.Parse(r.GetString(3)); var schema = r.GetString(4); var currentVersion = r.GetInt64(5); Guid? approval = r.IsDBNull(6) ? null : Guid.Parse(r.GetString(6)); var created = ParseTime(r.GetString(7)); var updated = ParseTime(r.GetString(8)); r.Close();
        using var versions = c.CreateCommand(); versions.Transaction = tx;
        versions.CommandText = "SELECT version,revision,status,content_json,content_digest,actor,reason,created_at_utc,updated_at_utc FROM book_plan_versions WHERE workspace_id=$w AND plan_id=$id ORDER BY version,revision;";
        versions.Parameters.AddWithValue("$w", w); versions.Parameters.AddWithValue("$id", id.ToString("D"));
        using var vr = versions.ExecuteReader(); var latest = new Dictionary<long, BookPlanVersion>();
        while (vr.Read()) { var item = new BookPlanVersion(vr.GetInt64(0), vr.GetInt64(1), Enum.Parse<BookPlanStatus>(vr.GetString(2), true), JsonSerializer.Deserialize<BookPlanContent>(vr.GetString(3)) ?? throw new InvalidOperationException("Invalid book plan content."), vr.IsDBNull(4) ? null : vr.GetString(4), vr.GetString(5), vr.GetString(6), ParseTime(vr.GetString(7)), ParseTime(vr.GetString(8))); latest[item.Version] = item; }
        return new(id, project, specification, specificationVersion, specificationMessage, w, schema, currentVersion, latest.Values.OrderBy(x => x.Version).ToArray(), approval, created, updated);
    }

    private static RequestRow? ReadRequest(SqliteConnection c, SqliteTransaction tx, Guid id)
    { using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="SELECT workspace_id,plan_id,operation,request_fingerprint,approval_message_id FROM book_plan_requests WHERE request_id=$id;"; cmd.Parameters.AddWithValue("$id",id.ToString("D")); using var r=cmd.ExecuteReader(); return r.Read()?new(r.GetString(0),Guid.Parse(r.GetString(1)),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:Guid.Parse(r.GetString(4))):null; }

    private static void InsertVersion(SqliteConnection c, SqliteTransaction tx, string w, Guid id, long version, long revision, BookPlanStatus status, BookPlanContent content, string? digest, string actor, string reason, DateTimeOffset at) =>
        Execute(c, tx, "INSERT INTO book_plan_versions(workspace_id,plan_id,version,revision,status,content_json,content_digest,actor,reason,created_at_utc,updated_at_utc) VALUES($w,$id,$v,$r,$s,$c,$d,$a,$reason,$at,$at);",("$w",w),("$id",id.ToString("D")),("$v",version),("$r",revision),("$s",status.ToString().ToUpperInvariant()),("$c",JsonSerializer.Serialize(content)),("$d",digest is null?DBNull.Value:digest),("$a",actor),("$reason",reason),("$at",Text(at)));

    private static void InsertRequest(SqliteConnection c, SqliteTransaction tx, Guid request, string w, Guid id, string op, string fingerprint, long version, long revision, Guid? message, DateTimeOffset at) =>
        Execute(c, tx, "INSERT INTO book_plan_requests(request_id,workspace_id,plan_id,operation,request_fingerprint,result_version,result_revision,approval_message_id,created_at_utc) VALUES($r,$w,$id,$op,$f,$v,$rev,$m,$at);",("$r",request.ToString("D")),("$w",w),("$id",id.ToString("D")),("$op",op),("$f",fingerprint),("$v",version),("$rev",revision),("$m",message is null?DBNull.Value:message.Value.ToString("D")),("$at",Text(at)));

    private static void RequireApprovedSpecification(SqliteConnection c, SqliteTransaction tx, BookPlanDraft d)
    { using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="SELECT current_version,approval_message_id FROM book_specifications WHERE workspace_id=$w AND specification_id=$s AND project_id=$p;"; cmd.Parameters.AddWithValue("$w",d.WorkspaceId); cmd.Parameters.AddWithValue("$s",d.SpecificationId.ToString("D")); cmd.Parameters.AddWithValue("$p",d.ProjectId.ToString("D")); using var r=cmd.ExecuteReader(); if(!r.Read()||r.GetInt64(0)!=d.SpecificationVersion||r.IsDBNull(1)||Guid.Parse(r.GetString(1))!=d.SpecificationApprovalMessageId) throw new BookPlanValidationException("An approved specification with matching version and approval evidence is required."); }

    private static void RequireRequest(RequestRow r, string op, string w, Guid id, string fingerprint)
    { if(r.Operation!=op||r.WorkspaceId!=w||r.PlanId!=id||r.Fingerprint!=fingerprint) throw new BookPlanConflictException("Request ID was reused with different immutable content."); }

    private static void RequireExpected(BookPlan p, long version, long revision)
    { if(p.CurrentVersion!=version||p.Current.Revision!=revision) throw new BookPlanConflictException("Expected book plan version or revision is stale."); }

    private static void ValidateDraft(BookPlanDraft d)
    { if(d is null||d.PlanId==Guid.Empty||d.ProjectId==Guid.Empty||d.SpecificationId==Guid.Empty||d.SpecificationVersion<=0||d.SpecificationApprovalMessageId==Guid.Empty||string.IsNullOrWhiteSpace(d.WorkspaceId)||string.IsNullOrWhiteSpace(d.SchemaVersion)||string.IsNullOrWhiteSpace(d.Actor)||string.IsNullOrWhiteSpace(d.RequestFingerprint)) throw new BookPlanValidationException("Book plan draft is incomplete."); ValidateContent(d.Content); }

    private static void ValidateRequest(Guid requestId,string workspaceId,Guid planId,string actor,string fingerprint)
    { if(requestId==Guid.Empty||planId==Guid.Empty||string.IsNullOrWhiteSpace(workspaceId)||string.IsNullOrWhiteSpace(actor)||string.IsNullOrWhiteSpace(fingerprint)) throw new BookPlanValidationException("Book plan request is incomplete."); }

    private static void ValidateContent(BookPlanContent content)
    {
        if(content is null||content.Parts is null||content.Chapters is null||content.GlobalConstraints is null||content.AcceptanceCriteria is null||content.Parts.Count==0||content.Chapters.Count==0||content.AcceptanceCriteria.Count==0) throw new BookPlanValidationException("Book plan content is incomplete.");
        var partKeys=new HashSet<string>(StringComparer.Ordinal); var partOrders=new HashSet<int>();
        foreach(var part in content.Parts){ if(string.IsNullOrWhiteSpace(part.Key)||part.Order<=0||string.IsNullOrWhiteSpace(part.Title)||string.IsNullOrWhiteSpace(part.Objective)||!partKeys.Add(part.Key)||!partOrders.Add(part.Order)) throw new BookPlanValidationException("Parts require unique keys and order with title and objective."); }
        var chapterKeys=new HashSet<string>(StringComparer.Ordinal); var chapterOrders=new HashSet<(string,int)>();
        foreach(var chapter in content.Chapters){ if(string.IsNullOrWhiteSpace(chapter.Key)||!partKeys.Contains(chapter.PartKey)||chapter.Order<=0||string.IsNullOrWhiteSpace(chapter.Title)||string.IsNullOrWhiteSpace(chapter.Objective)||string.IsNullOrWhiteSpace(chapter.Audience)||chapter.Deliverables is null||chapter.Deliverables.Count==0||chapter.Constraints is null||chapter.AcceptanceCriteria is null||chapter.AcceptanceCriteria.Count==0||chapter.DependsOn is null||!chapterKeys.Add(chapter.Key)||!chapterOrders.Add((chapter.PartKey,chapter.Order))) throw new BookPlanValidationException("Chapters require unique keys and part-local order with complete outcomes."); }
        foreach(var chapter in content.Chapters) foreach(var dependency in chapter.DependsOn) if(dependency==chapter.Key||!chapterKeys.Contains(dependency)) throw new BookPlanValidationException("Chapter dependency is missing or self-referential.");
        var map=content.Chapters.ToDictionary(x=>x.Key,x=>x.DependsOn,StringComparer.Ordinal); var visiting=new HashSet<string>(StringComparer.Ordinal); var visited=new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string key){ if(visited.Contains(key)) return false; if(!visiting.Add(key)) return true; foreach(var dep in map[key]) if(Visit(dep)) return true; visiting.Remove(key); visited.Add(key); return false; }
        if(map.Keys.Any(Visit)) throw new BookPlanValidationException("Chapter dependencies must be acyclic.");
    }

    private static bool Same(BookPlanContent a,BookPlanContent b)=>JsonSerializer.Serialize(a)==JsonSerializer.Serialize(b);
    private static string Digest(BookPlanContent content)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content)))).ToLowerInvariant();
    private static Guid DeterministicMessageId(Guid requestId)=>new(SHA256.HashData(Encoding.UTF8.GetBytes("book-plan-approval:"+requestId.ToString("D")))[..16]);
    private static Guid DeterministicCreateRequestId(BookPlanDraft draft)=>new(SHA256.HashData(Encoding.UTF8.GetBytes("book-plan-create:"+draft.WorkspaceId+":"+draft.PlanId.ToString("D")))[..16]);
    private static string Text(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string value)=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
    private static void Execute(SqliteConnection c,SqliteTransaction tx,string sql,params (string Name,object Value)[] values){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var item in values)cmd.Parameters.AddWithValue(item.Name,item.Value);cmd.ExecuteNonQuery();}
}
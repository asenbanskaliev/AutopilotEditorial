using System.Globalization;
using System.Text.Json;
using BookStudio.Application.Authoring;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Authoring;

public sealed class SqliteDiscoverySessionStore : IDiscoverySessionStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteDiscoverySessionStore(SqliteConnectionFactory factory, int writeQueueCapacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, writeQueueCapacity);
    }

    public ValueTask<DiscoveryCreateResult> CreateAsync(DiscoverySessionDraft draft, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateDraft(draft);
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(c, tx, draft.WorkspaceId, draft.SessionId);
            if (existing is not null)
            {
                if (existing.ProjectId == draft.ProjectId && existing.SchemaVersion == draft.SchemaVersion && SameQuestions(existing.Questions, draft.Questions)) return new DiscoveryCreateResult(existing, true);
                throw new DiscoveryConflictException("Discovery identity already exists with different immutable content.");
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO discovery_sessions(workspace_id,session_id,project_id,schema_version,request_fingerprint,status,version,completion_message_id,created_at_utc,updated_at_utc) VALUES($w,$s,$p,$v,$f,'OPEN',1,NULL,$at,$at);";
                cmd.Parameters.AddWithValue("$w", draft.WorkspaceId); cmd.Parameters.AddWithValue("$s", draft.SessionId.ToString("D")); cmd.Parameters.AddWithValue("$p", draft.ProjectId.ToString("D")); cmd.Parameters.AddWithValue("$v", draft.SchemaVersion); cmd.Parameters.AddWithValue("$f", draft.RequestFingerprint); cmd.Parameters.AddWithValue("$at", Text(at)); cmd.ExecuteNonQuery();
            }
            foreach (var q in draft.Questions.OrderBy(x => x.Order))
            {
                using var cmd = c.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO discovery_questions(workspace_id,session_id,question_key,question_order,question_type,required,prompt) VALUES($w,$s,$k,$o,$t,$r,$p);";
                cmd.Parameters.AddWithValue("$w", draft.WorkspaceId); cmd.Parameters.AddWithValue("$s", draft.SessionId.ToString("D")); cmd.Parameters.AddWithValue("$k", q.Key); cmd.Parameters.AddWithValue("$o", q.Order); cmd.Parameters.AddWithValue("$t", TypeText(q.Type)); cmd.Parameters.AddWithValue("$r", q.Required ? 1 : 0); cmd.Parameters.AddWithValue("$p", q.Prompt); cmd.ExecuteNonQuery();
            }
            return new DiscoveryCreateResult(Require(c, tx, draft.WorkspaceId, draft.SessionId), false);
        }, ct);
    }

    public ValueTask<DiscoverySession> AnswerAsync(DiscoveryAnswerCommand command, DateTimeOffset at, CancellationToken ct = default) => Mutate(command.RequestId, command.WorkspaceId, command.SessionId, "ANSWER", command.RequestFingerprint, at, (c, tx, session) =>
    {
        EnsureOpen(session); var question = session.Questions.SingleOrDefault(q => q.Key == command.QuestionKey) ?? throw new DiscoveryConflictException("Unknown discovery question.");
        ValidateAnswer(question, command.AnswerJson);
        var version = session.Answers.Where(a => a.QuestionKey == command.QuestionKey).Select(a => a.Version).DefaultIfEmpty(0).Max() + 1;
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO discovery_answers(workspace_id,session_id,question_key,answer_version,answer_json,actor,answered_at_utc) VALUES($w,$s,$k,$v,$a,$actor,$at);";
        cmd.Parameters.AddWithValue("$w", command.WorkspaceId); cmd.Parameters.AddWithValue("$s", command.SessionId.ToString("D")); cmd.Parameters.AddWithValue("$k", command.QuestionKey); cmd.Parameters.AddWithValue("$v", version); cmd.Parameters.AddWithValue("$a", command.AnswerJson); cmd.Parameters.AddWithValue("$actor", command.Actor); cmd.Parameters.AddWithValue("$at", Text(at)); cmd.ExecuteNonQuery();
    }, ct);

    public ValueTask<DiscoverySession> DecideAsync(DiscoveryDecisionCommand command, DateTimeOffset at, CancellationToken ct = default) => Mutate(command.RequestId, command.WorkspaceId, command.SessionId, "DECIDE", command.RequestFingerprint, at, (c, tx, session) =>
    {
        EnsureOpen(session); if (string.IsNullOrWhiteSpace(command.DecisionKey) || string.IsNullOrWhiteSpace(command.SelectedOption) || string.IsNullOrWhiteSpace(command.Rationale) || string.IsNullOrWhiteSpace(command.Actor)) throw new ArgumentException("Discovery decision is invalid.");
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO discovery_decisions(workspace_id,session_id,decision_key,selected_option,rationale,actor,evidence_reference,decided_at_utc) VALUES($w,$s,$k,$o,$r,$a,$e,$at) ON CONFLICT(workspace_id,session_id,decision_key) DO UPDATE SET selected_option=excluded.selected_option,rationale=excluded.rationale,actor=excluded.actor,evidence_reference=excluded.evidence_reference,decided_at_utc=excluded.decided_at_utc;";
        cmd.Parameters.AddWithValue("$w", command.WorkspaceId); cmd.Parameters.AddWithValue("$s", command.SessionId.ToString("D")); cmd.Parameters.AddWithValue("$k", command.DecisionKey); cmd.Parameters.AddWithValue("$o", command.SelectedOption); cmd.Parameters.AddWithValue("$r", command.Rationale); cmd.Parameters.AddWithValue("$a", command.Actor); cmd.Parameters.AddWithValue("$e", (object?)command.EvidenceReference ?? DBNull.Value); cmd.Parameters.AddWithValue("$at", Text(at)); cmd.ExecuteNonQuery();
    }, ct);

    public ValueTask<DiscoverySession> SetOpenItemAsync(DiscoveryOpenItemCommand command, DateTimeOffset at, CancellationToken ct = default) => Mutate(command.RequestId, command.WorkspaceId, command.SessionId, "OPEN_ITEM", command.RequestFingerprint, at, (c, tx, session) =>
    {
        EnsureOpen(session); if (string.IsNullOrWhiteSpace(command.ItemKey) || string.IsNullOrWhiteSpace(command.Description) || string.IsNullOrWhiteSpace(command.Actor)) throw new ArgumentException("Discovery open item is invalid.");
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO discovery_open_items(workspace_id,session_id,item_key,description,required,resolved,actor,updated_at_utc) VALUES($w,$s,$k,$d,$r,$x,$a,$at) ON CONFLICT(workspace_id,session_id,item_key) DO UPDATE SET description=excluded.description,required=excluded.required,resolved=excluded.resolved,actor=excluded.actor,updated_at_utc=excluded.updated_at_utc;";
        cmd.Parameters.AddWithValue("$w", command.WorkspaceId); cmd.Parameters.AddWithValue("$s", command.SessionId.ToString("D")); cmd.Parameters.AddWithValue("$k", command.ItemKey); cmd.Parameters.AddWithValue("$d", command.Description); cmd.Parameters.AddWithValue("$r", command.Required ? 1 : 0); cmd.Parameters.AddWithValue("$x", command.Resolved ? 1 : 0); cmd.Parameters.AddWithValue("$a", command.Actor); cmd.Parameters.AddWithValue("$at", Text(at)); cmd.ExecuteNonQuery();
    }, ct);

    public ValueTask<DiscoveryCompleteResult> CompleteAsync(DiscoveryCompleteCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested(); ValidateRequest(command.RequestId, command.WorkspaceId, command.SessionId, command.Actor, command.RequestFingerprint);
            var prior = ReadRequest(c, tx, command.RequestId);
            if (prior is not null) { RequireRequest(prior, "COMPLETE", command.WorkspaceId, command.SessionId, command.RequestFingerprint); var replay = Require(c, tx, command.WorkspaceId, command.SessionId); return new DiscoveryCompleteResult(replay, true, replay.CompletionMessageId!.Value); }
            var session = Require(c, tx, command.WorkspaceId, command.SessionId); EnsureOpen(session);
            var latest = session.Answers.GroupBy(a => a.QuestionKey).ToDictionary(g => g.Key, g => g.MaxBy(a => a.Version)!);
            if (session.Questions.Any(q => q.Required && !latest.ContainsKey(q.Key))) throw new DiscoveryCompletionException("Required discovery questions are unanswered.");
            if (session.OpenItems.Any(i => i.Required && !i.Resolved)) throw new DiscoveryCompletionException("Required discovery items remain unresolved.");
            var messageId = DeterministicMessageId(command.RequestId);
            using (var update = c.CreateCommand()) { update.Transaction = tx; update.CommandText = "UPDATE discovery_sessions SET status='COMPLETED',version=version+1,completion_message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND session_id=$s;"; update.Parameters.AddWithValue("$m", messageId.ToString("D")); update.Parameters.AddWithValue("$at", Text(at)); update.Parameters.AddWithValue("$w", command.WorkspaceId); update.Parameters.AddWithValue("$s", command.SessionId.ToString("D")); update.ExecuteNonQuery(); }
            InsertRequest(c, tx, command.RequestId, command.WorkspaceId, command.SessionId, "COMPLETE", command.RequestFingerprint, at);
            using (var outbox = c.CreateCommand()) { outbox.Transaction = tx; outbox.CommandText = "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($m,'editorial.discovery.completed','1.0.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);"; outbox.Parameters.AddWithValue("$m", messageId.ToString("D")); outbox.Parameters.AddWithValue("$p", JsonSerializer.Serialize(new { command.WorkspaceId, command.SessionId, session.ProjectId, completedBy = command.Actor })); outbox.Parameters.AddWithValue("$at", Text(at)); outbox.ExecuteNonQuery(); }
            return new DiscoveryCompleteResult(Require(c, tx, command.WorkspaceId, command.SessionId), false, messageId);
        }, ct);
    }

    public async ValueTask<DiscoverySession?> GetAsync(string workspaceId, Guid sessionId, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); using var c = _factory.OpenConnection(); return await Task.FromResult(Read(c, null, workspaceId, sessionId)).ConfigureAwait(false); }
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false); }

    private ValueTask<DiscoverySession> Mutate(Guid requestId, string workspaceId, Guid sessionId, string operation, string fingerprint, DateTimeOffset at, Action<SqliteConnection,SqliteTransaction,DiscoverySession> action, CancellationToken ct)
    {
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested(); ValidateRequest(requestId, workspaceId, sessionId, "actor", fingerprint);
            var prior = ReadRequest(c, tx, requestId);
            if (prior is not null) { RequireRequest(prior, operation, workspaceId, sessionId, fingerprint); return Require(c, tx, workspaceId, sessionId); }
            var session = Require(c, tx, workspaceId, sessionId); action(c, tx, session); InsertRequest(c, tx, requestId, workspaceId, sessionId, operation, fingerprint, at);
            using var update = c.CreateCommand(); update.Transaction = tx; update.CommandText = "UPDATE discovery_sessions SET version=version+1,updated_at_utc=$at WHERE workspace_id=$w AND session_id=$s;"; update.Parameters.AddWithValue("$at", Text(at)); update.Parameters.AddWithValue("$w", workspaceId); update.Parameters.AddWithValue("$s", sessionId.ToString("D")); update.ExecuteNonQuery();
            return Require(c, tx, workspaceId, sessionId);
        }, ct);
    }

    private sealed record RequestRow(string WorkspaceId, Guid SessionId, string Operation, string Fingerprint);
    private static RequestRow? ReadRequest(SqliteConnection c, SqliteTransaction tx, Guid id) { using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="SELECT workspace_id,session_id,operation,request_fingerprint FROM discovery_requests WHERE request_id=$id;"; cmd.Parameters.AddWithValue("$id",id.ToString("D")); using var r=cmd.ExecuteReader(); return r.Read()?new(r.GetString(0),Guid.Parse(r.GetString(1)),r.GetString(2),r.GetString(3)):null; }
    private static void RequireRequest(RequestRow r,string op,string w,Guid s,string f) { if(r.Operation!=op||r.WorkspaceId!=w||r.SessionId!=s||r.Fingerprint!=f) throw new DiscoveryConflictException("Request ID was reused with different immutable content."); }
    private static void InsertRequest(SqliteConnection c,SqliteTransaction tx,Guid id,string w,Guid s,string op,string f,DateTimeOffset at) { using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="INSERT INTO discovery_requests(request_id,workspace_id,session_id,operation,request_fingerprint,created_at_utc) VALUES($id,$w,$s,$o,$f,$at);"; cmd.Parameters.AddWithValue("$id",id.ToString("D"));cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$s",s.ToString("D"));cmd.Parameters.AddWithValue("$o",op);cmd.Parameters.AddWithValue("$f",f);cmd.Parameters.AddWithValue("$at",Text(at));cmd.ExecuteNonQuery(); }
    private static DiscoverySession Require(SqliteConnection c,SqliteTransaction tx,string w,Guid s)=>Read(c,tx,w,s)??throw new KeyNotFoundException("Discovery session was not found.");
    private static DiscoverySession? Read(SqliteConnection c,SqliteTransaction? tx,string w,Guid s)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT project_id,schema_version,status,version,completion_message_id,created_at_utc,updated_at_utc FROM discovery_sessions WHERE workspace_id=$w AND session_id=$s;";cmd.Parameters.AddWithValue("$w",w);cmd.Parameters.AddWithValue("$s",s.ToString("D"));using var r=cmd.ExecuteReader();if(!r.Read())return null;
        var project=Guid.Parse(r.GetString(0));var schema=r.GetString(1);var status=ParseStatus(r.GetString(2));var version=r.GetInt64(3);var message=r.IsDBNull(4)?null:Guid.Parse(r.GetString(4));var created=ParseTime(r.GetString(5));var updated=ParseTime(r.GetString(6));r.Close();
        var questions=new List<DiscoveryQuestion>();using(var q=c.CreateCommand()){q.Transaction=tx;q.CommandText="SELECT question_key,question_order,question_type,required,prompt FROM discovery_questions WHERE workspace_id=$w AND session_id=$s ORDER BY question_order;";q.Parameters.AddWithValue("$w",w);q.Parameters.AddWithValue("$s",s.ToString("D"));using var qr=q.ExecuteReader();while(qr.Read())questions.Add(new(qr.GetString(0),qr.GetInt32(1),ParseType(qr.GetString(2)),qr.GetInt32(3)==1,qr.GetString(4)));}
        var answers=new List<DiscoveryAnswer>();using(var a=c.CreateCommand()){a.Transaction=tx;a.CommandText="SELECT question_key,answer_version,answer_json,actor,answered_at_utc FROM discovery_answers WHERE workspace_id=$w AND session_id=$s ORDER BY question_key,answer_version;";a.Parameters.AddWithValue("$w",w);a.Parameters.AddWithValue("$s",s.ToString("D"));using var ar=a.ExecuteReader();while(ar.Read())answers.Add(new(ar.GetString(0),ar.GetInt32(1),ar.GetString(2),ar.GetString(3),ParseTime(ar.GetString(4))));}
        var decisions=new List<DiscoveryDecision>();using(var d=c.CreateCommand()){d.Transaction=tx;d.CommandText="SELECT decision_key,selected_option,rationale,actor,evidence_reference,decided_at_utc FROM discovery_decisions WHERE workspace_id=$w AND session_id=$s ORDER BY decision_key;";d.Parameters.AddWithValue("$w",w);d.Parameters.AddWithValue("$s",s.ToString("D"));using var dr=d.ExecuteReader();while(dr.Read())decisions.Add(new(dr.GetString(0),dr.GetString(1),dr.GetString(2),dr.GetString(3),dr.IsDBNull(4)?null:dr.GetString(4),ParseTime(dr.GetString(5))));}
        var items=new List<DiscoveryOpenItem>();using(var i=c.CreateCommand()){i.Transaction=tx;i.CommandText="SELECT item_key,description,required,resolved,actor,updated_at_utc FROM discovery_open_items WHERE workspace_id=$w AND session_id=$s ORDER BY item_key;";i.Parameters.AddWithValue("$w",w);i.Parameters.AddWithValue("$s",s.ToString("D"));using var ir=i.ExecuteReader();while(ir.Read())items.Add(new(ir.GetString(0),ir.GetString(1),ir.GetInt32(2)==1,ir.GetInt32(3)==1,ir.GetString(4),ParseTime(ir.GetString(5))));}
        return new(s,project,w,schema,status,version,questions,answers,decisions,items,message,created,updated);
    }

    private static void ValidateDraft(DiscoverySessionDraft d){if(d is null||d.SessionId==Guid.Empty||d.ProjectId==Guid.Empty||string.IsNullOrWhiteSpace(d.WorkspaceId)||string.IsNullOrWhiteSpace(d.SchemaVersion)||string.IsNullOrWhiteSpace(d.RequestFingerprint)||d.Questions is null||d.Questions.Count==0||d.Questions.Any(q=>string.IsNullOrWhiteSpace(q.Key)||q.Order<0||string.IsNullOrWhiteSpace(q.Prompt))||d.Questions.GroupBy(q=>q.Key).Any(g=>g.Count()>1))throw new ArgumentException("Discovery draft is invalid.");}
    private static void ValidateRequest(Guid id,string w,Guid s,string actor,string f){if(id==Guid.Empty||s==Guid.Empty||string.IsNullOrWhiteSpace(w)||string.IsNullOrWhiteSpace(actor)||string.IsNullOrWhiteSpace(f))throw new ArgumentException("Discovery request is invalid.");}
    private static void ValidateAnswer(DiscoveryQuestion q,string json){if(string.IsNullOrWhiteSpace(json))throw new ArgumentException("Discovery answer is invalid.");try{using var doc=JsonDocument.Parse(json);if(q.Type==DiscoveryQuestionType.Boolean&&doc.RootElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)throw new ArgumentException("Expected boolean answer.");if(q.Type==DiscoveryQuestionType.Number&&doc.RootElement.ValueKind!=JsonValueKind.Number)throw new ArgumentException("Expected numeric answer.");}catch(JsonException e){throw new ArgumentException("Discovery answer must be valid JSON.",e);}}
    private static void EnsureOpen(DiscoverySession s){if(s.Status!=DiscoverySessionStatus.Open)throw new DiscoveryImmutableException(s.SessionId);}
    private static bool SameQuestions(IReadOnlyList<DiscoveryQuestion>a,IReadOnlyList<DiscoveryQuestion>b)=>a.OrderBy(x=>x.Key).SequenceEqual(b.OrderBy(x=>x.Key));
    private static Guid DeterministicMessageId(Guid id){var b=id.ToByteArray();b[0]^=0x51;b[15]^=0x15;return new Guid(b);}
    private static string TypeText(DiscoveryQuestionType v)=>v.ToString().ToUpperInvariant();
    private static DiscoveryQuestionType ParseType(string v)=>Enum.Parse<DiscoveryQuestionType>(v,true);
    private static DiscoverySessionStatus ParseStatus(string v)=>v switch{"OPEN"=>DiscoverySessionStatus.Open,"COMPLETED"=>DiscoverySessionStatus.Completed,"CANCELLED"=>DiscoverySessionStatus.Cancelled,_=>throw new InvalidOperationException("Unknown discovery status.")};
    private static string Text(DateTimeOffset v)=>v.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string v)=>DateTimeOffset.Parse(v,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
}

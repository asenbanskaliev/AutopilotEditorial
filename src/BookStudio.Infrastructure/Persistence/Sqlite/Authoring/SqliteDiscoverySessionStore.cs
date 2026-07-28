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
                if (existing.ProjectId == draft.ProjectId && existing.SchemaVersion == draft.SchemaVersion && SameQuestions(existing.Questions, draft.Questions))
                    return new DiscoveryCreateResult(existing, true);
                throw new DiscoveryConflictException("Discovery identity already exists with different immutable content.");
            }

            Execute(c, tx, "INSERT INTO discovery_sessions(workspace_id,session_id,project_id,schema_version,request_fingerprint,status,version,completion_message_id,created_at_utc,updated_at_utc) VALUES($w,$s,$p,$v,$f,'OPEN',1,NULL,$at,$at);",
                ("$w", draft.WorkspaceId), ("$s", draft.SessionId.ToString("D")), ("$p", draft.ProjectId.ToString("D")), ("$v", draft.SchemaVersion), ("$f", draft.RequestFingerprint), ("$at", Text(at)));
            foreach (var q in draft.Questions.OrderBy(x => x.Order))
                Execute(c, tx, "INSERT INTO discovery_questions(workspace_id,session_id,question_key,question_order,question_type,required,prompt) VALUES($w,$s,$k,$o,$t,$r,$p);",
                    ("$w", draft.WorkspaceId), ("$s", draft.SessionId.ToString("D")), ("$k", q.Key), ("$o", q.Order), ("$t", q.Type.ToString().ToUpperInvariant()), ("$r", q.Required ? 1 : 0), ("$p", q.Prompt));
            return new DiscoveryCreateResult(Require(c, tx, draft.WorkspaceId, draft.SessionId), false);
        }, ct);
    }

    public ValueTask<DiscoverySession> AnswerAsync(DiscoveryAnswerCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.RequestId, command.WorkspaceId, command.SessionId, "ANSWER", command.RequestFingerprint, at, (c, tx, session) =>
        {
            EnsureOpen(session);
            var question = session.Questions.SingleOrDefault(q => q.Key == command.QuestionKey) ?? throw new DiscoveryConflictException("Unknown discovery question.");
            ValidateAnswer(question, command.AnswerJson);
            var version = session.Answers.Where(a => a.QuestionKey == command.QuestionKey).Select(a => a.Version).DefaultIfEmpty().Max() + 1;
            Execute(c, tx, "INSERT INTO discovery_answers(workspace_id,session_id,question_key,answer_version,answer_json,actor,answered_at_utc) VALUES($w,$s,$k,$v,$a,$actor,$at);",
                ("$w", command.WorkspaceId), ("$s", command.SessionId.ToString("D")), ("$k", command.QuestionKey), ("$v", version), ("$a", command.AnswerJson), ("$actor", command.Actor), ("$at", Text(at)));
        }, ct);

    public ValueTask<DiscoverySession> DecideAsync(DiscoveryDecisionCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.RequestId, command.WorkspaceId, command.SessionId, "DECIDE", command.RequestFingerprint, at, (c, tx, session) =>
        {
            EnsureOpen(session);
            RequireText(command.DecisionKey, command.SelectedOption, command.Rationale, command.Actor);
            Execute(c, tx, "INSERT INTO discovery_decisions(workspace_id,session_id,decision_key,selected_option,rationale,actor,evidence_reference,decided_at_utc) VALUES($w,$s,$k,$o,$r,$a,$e,$at) ON CONFLICT(workspace_id,session_id,decision_key) DO UPDATE SET selected_option=excluded.selected_option,rationale=excluded.rationale,actor=excluded.actor,evidence_reference=excluded.evidence_reference,decided_at_utc=excluded.decided_at_utc;",
                ("$w", command.WorkspaceId), ("$s", command.SessionId.ToString("D")), ("$k", command.DecisionKey), ("$o", command.SelectedOption), ("$r", command.Rationale), ("$a", command.Actor), ("$e", (object?)command.EvidenceReference ?? DBNull.Value), ("$at", Text(at)));
        }, ct);

    public ValueTask<DiscoverySession> SetOpenItemAsync(DiscoveryOpenItemCommand command, DateTimeOffset at, CancellationToken ct = default) =>
        Mutate(command.RequestId, command.WorkspaceId, command.SessionId, "OPEN_ITEM", command.RequestFingerprint, at, (c, tx, session) =>
        {
            EnsureOpen(session);
            RequireText(command.ItemKey, command.Description, command.Actor);
            Execute(c, tx, "INSERT INTO discovery_open_items(workspace_id,session_id,item_key,description,required,resolved,actor,updated_at_utc) VALUES($w,$s,$k,$d,$r,$x,$a,$at) ON CONFLICT(workspace_id,session_id,item_key) DO UPDATE SET description=excluded.description,required=excluded.required,resolved=excluded.resolved,actor=excluded.actor,updated_at_utc=excluded.updated_at_utc;",
                ("$w", command.WorkspaceId), ("$s", command.SessionId.ToString("D")), ("$k", command.ItemKey), ("$d", command.Description), ("$r", command.Required ? 1 : 0), ("$x", command.Resolved ? 1 : 0), ("$a", command.Actor), ("$at", Text(at)));
        }, ct);

    public ValueTask<DiscoveryCompleteResult> CompleteAsync(DiscoveryCompleteCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            ValidateRequest(command.RequestId, command.WorkspaceId, command.SessionId, command.Actor, command.RequestFingerprint);
            var prior = ReadRequest(c, tx, command.RequestId);
            if (prior is not null)
            {
                RequireRequest(prior, "COMPLETE", command.WorkspaceId, command.SessionId, command.RequestFingerprint);
                var replay = Require(c, tx, command.WorkspaceId, command.SessionId);
                return new DiscoveryCompleteResult(replay, true, replay.CompletionMessageId ?? throw new InvalidOperationException("Completion message missing."));
            }

            var session = Require(c, tx, command.WorkspaceId, command.SessionId);
            EnsureOpen(session);
            var latest = session.Answers.GroupBy(a => a.QuestionKey).ToDictionary(g => g.Key, g => g.MaxBy(a => a.Version)!);
            if (session.Questions.Any(q => q.Required && !latest.ContainsKey(q.Key))) throw new DiscoveryCompletionException("Required discovery questions are unanswered.");
            if (session.OpenItems.Any(i => i.Required && !i.Resolved)) throw new DiscoveryCompletionException("Required discovery items remain unresolved.");

            var messageId = DeterministicMessageId(command.RequestId);
            Execute(c, tx, "UPDATE discovery_sessions SET status='COMPLETED',version=version+1,completion_message_id=$m,updated_at_utc=$at WHERE workspace_id=$w AND session_id=$s;",
                ("$m", messageId.ToString("D")), ("$at", Text(at)), ("$w", command.WorkspaceId), ("$s", command.SessionId.ToString("D")));
            InsertRequest(c, tx, command.RequestId, command.WorkspaceId, command.SessionId, "COMPLETE", command.RequestFingerprint, at);
            Execute(c, tx, "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($m,'editorial.discovery.completed','1.0.0',$p,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);",
                ("$m", messageId.ToString("D")), ("$p", JsonSerializer.Serialize(new { command.WorkspaceId, command.SessionId, session.ProjectId, completedBy = command.Actor })), ("$at", Text(at)));
            return new DiscoveryCompleteResult(Require(c, tx, command.WorkspaceId, command.SessionId), false, messageId);
        }, ct);
    }

    public async ValueTask<DiscoverySession?> GetAsync(string workspaceId, Guid sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var c = _factory.OpenConnection();
        return await Task.FromResult(Read(c, null, workspaceId, sessionId)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false);
    }

    private ValueTask<DiscoverySession> Mutate(Guid requestId, string workspaceId, Guid sessionId, string operation, string fingerprint, DateTimeOffset at, Action<SqliteConnection, SqliteTransaction, DiscoverySession> action, CancellationToken ct)
    {
        return _queue.ExecuteInTransactionAsync((c, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            ValidateRequest(requestId, workspaceId, sessionId, "actor", fingerprint);
            var prior = ReadRequest(c, tx, requestId);
            if (prior is not null)
            {
                RequireRequest(prior, operation, workspaceId, sessionId, fingerprint);
                return Require(c, tx, workspaceId, sessionId);
            }
            var session = Require(c, tx, workspaceId, sessionId);
            action(c, tx, session);
            InsertRequest(c, tx, requestId, workspaceId, sessionId, operation, fingerprint, at);
            Execute(c, tx, "UPDATE discovery_sessions SET version=version+1,updated_at_utc=$at WHERE workspace_id=$w AND session_id=$s;", ("$at", Text(at)), ("$w", workspaceId), ("$s", sessionId.ToString("D")));
            return Require(c, tx, workspaceId, sessionId);
        }, ct);
    }

    private sealed record RequestRow(string WorkspaceId, Guid SessionId, string Operation, string Fingerprint);

    private static DiscoverySession Require(SqliteConnection c, SqliteTransaction tx, string workspaceId, Guid sessionId) =>
        Read(c, tx, workspaceId, sessionId) ?? throw new KeyNotFoundException("Discovery session was not found.");

    private static DiscoverySession? Read(SqliteConnection c, SqliteTransaction? tx, string workspaceId, Guid sessionId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT project_id,schema_version,status,version,completion_message_id,created_at_utc,updated_at_utc FROM discovery_sessions WHERE workspace_id=$w AND session_id=$s;";
        cmd.Parameters.AddWithValue("$w", workspaceId); cmd.Parameters.AddWithValue("$s", sessionId.ToString("D"));
        using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
        var projectId = Guid.Parse(r.GetString(0)); var schema = r.GetString(1); var status = ParseStatus(r.GetString(2)); var version = r.GetInt64(3);
        Guid? messageId = r.IsDBNull(4) ? null : Guid.Parse(r.GetString(4));
        var created = ParseTime(r.GetString(5)); var updated = ParseTime(r.GetString(6)); r.Close();
        return new DiscoverySession(sessionId, projectId, workspaceId, schema, status, version,
            ReadQuestions(c, tx, workspaceId, sessionId), ReadAnswers(c, tx, workspaceId, sessionId), ReadDecisions(c, tx, workspaceId, sessionId), ReadItems(c, tx, workspaceId, sessionId), messageId, created, updated);
    }

    private static IReadOnlyList<DiscoveryQuestion> ReadQuestions(SqliteConnection c, SqliteTransaction? tx, string w, Guid s)
    {
        var list = new List<DiscoveryQuestion>(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT question_key,question_order,question_type,required,prompt FROM discovery_questions WHERE workspace_id=$w AND session_id=$s ORDER BY question_order;"; cmd.Parameters.AddWithValue("$w", w); cmd.Parameters.AddWithValue("$s", s.ToString("D"));
        using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(new(r.GetString(0), r.GetInt32(1), Enum.Parse<DiscoveryQuestionType>(r.GetString(2), true), r.GetInt32(3) == 1, r.GetString(4))); return list;
    }
    private static IReadOnlyList<DiscoveryAnswer> ReadAnswers(SqliteConnection c, SqliteTransaction? tx, string w, Guid s)
    {
        var list = new List<DiscoveryAnswer>(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT question_key,answer_version,answer_json,actor,answered_at_utc FROM discovery_answers WHERE workspace_id=$w AND session_id=$s ORDER BY question_key,answer_version;"; cmd.Parameters.AddWithValue("$w", w); cmd.Parameters.AddWithValue("$s", s.ToString("D"));
        using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(new(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3), ParseTime(r.GetString(4)))); return list;
    }
    private static IReadOnlyList<DiscoveryDecision> ReadDecisions(SqliteConnection c, SqliteTransaction? tx, string w, Guid s)
    {
        var list = new List<DiscoveryDecision>(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT decision_key,selected_option,rationale,actor,evidence_reference,decided_at_utc FROM discovery_decisions WHERE workspace_id=$w AND session_id=$s ORDER BY decision_key;"; cmd.Parameters.AddWithValue("$w", w); cmd.Parameters.AddWithValue("$s", s.ToString("D"));
        using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), ParseTime(r.GetString(5)))); return list;
    }
    private static IReadOnlyList<DiscoveryOpenItem> ReadItems(SqliteConnection c, SqliteTransaction? tx, string w, Guid s)
    {
        var list = new List<DiscoveryOpenItem>(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT item_key,description,required,resolved,actor,updated_at_utc FROM discovery_open_items WHERE workspace_id=$w AND session_id=$s ORDER BY item_key;"; cmd.Parameters.AddWithValue("$w", w); cmd.Parameters.AddWithValue("$s", s.ToString("D"));
        using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), r.GetInt32(2) == 1, r.GetInt32(3) == 1, r.GetString(4), ParseTime(r.GetString(5)))); return list;
    }

    private static RequestRow? ReadRequest(SqliteConnection c, SqliteTransaction tx, Guid id)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT workspace_id,session_id,operation,request_fingerprint FROM discovery_requests WHERE request_id=$id;"; cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        using var r = cmd.ExecuteReader(); return r.Read() ? new(r.GetString(0), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3)) : null;
    }
    private static void InsertRequest(SqliteConnection c, SqliteTransaction tx, Guid id, string w, Guid s, string op, string fingerprint, DateTimeOffset at) =>
        Execute(c, tx, "INSERT INTO discovery_requests(request_id,workspace_id,session_id,operation,request_fingerprint,created_at_utc) VALUES($id,$w,$s,$o,$f,$at);", ("$id", id.ToString("D")), ("$w", w), ("$s", s.ToString("D")), ("$o", op), ("$f", fingerprint), ("$at", Text(at)));
    private static void RequireRequest(RequestRow row, string op, string w, Guid s, string fingerprint)
    { if (row.Operation != op || row.WorkspaceId != w || row.SessionId != s || row.Fingerprint != fingerprint) throw new DiscoveryConflictException("Request ID was reused with different immutable content."); }

    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] values)
    { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var value in values) cmd.Parameters.AddWithValue(value.Name, value.Value); cmd.ExecuteNonQuery(); }
    private static void EnsureOpen(DiscoverySession session) { if (session.Status != DiscoverySessionStatus.Open) throw new DiscoveryImmutableException(session.SessionId); }
    private static void RequireText(params string[] values) { if (values.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Discovery command is invalid."); }
    private static void ValidateDraft(DiscoverySessionDraft d)
    { if (d is null || d.SessionId == Guid.Empty || d.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(d.WorkspaceId) || string.IsNullOrWhiteSpace(d.SchemaVersion) || string.IsNullOrWhiteSpace(d.RequestFingerprint) || d.Questions is null || d.Questions.Count == 0 || d.Questions.Any(q => string.IsNullOrWhiteSpace(q.Key) || q.Order < 0 || string.IsNullOrWhiteSpace(q.Prompt)) || d.Questions.GroupBy(q => q.Key).Any(g => g.Count() > 1)) throw new ArgumentException("Discovery draft is invalid."); }
    private static void ValidateRequest(Guid id, string w, Guid s, string actor, string fingerprint)
    { if (id == Guid.Empty || s == Guid.Empty || string.IsNullOrWhiteSpace(w) || string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(fingerprint)) throw new ArgumentException("Discovery request is invalid."); }
    private static void ValidateAnswer(DiscoveryQuestion q, string json)
    { try { using var doc = JsonDocument.Parse(json); if (q.Type == DiscoveryQuestionType.Boolean && doc.RootElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False) throw new ArgumentException("Expected boolean answer."); if (q.Type == DiscoveryQuestionType.Number && doc.RootElement.ValueKind != JsonValueKind.Number) throw new ArgumentException("Expected numeric answer."); } catch (JsonException e) { throw new ArgumentException("Discovery answer must be valid JSON.", e); } }
    private static bool SameQuestions(IReadOnlyList<DiscoveryQuestion> a, IReadOnlyList<DiscoveryQuestion> b) => a.OrderBy(x => x.Key).SequenceEqual(b.OrderBy(x => x.Key));
    private static Guid DeterministicMessageId(Guid id) { var bytes = id.ToByteArray(); bytes[0] ^= 0x51; bytes[15] ^= 0x15; return new Guid(bytes); }
    private static DiscoverySessionStatus ParseStatus(string value) => value switch { "OPEN" => DiscoverySessionStatus.Open, "COMPLETED" => DiscoverySessionStatus.Completed, "CANCELLED" => DiscoverySessionStatus.Cancelled, _ => throw new InvalidOperationException("Unknown discovery status.") };
    private static string Text(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

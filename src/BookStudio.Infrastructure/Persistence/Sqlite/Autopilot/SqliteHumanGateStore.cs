using System.Globalization;
using System.Text.Json;
using BookStudio.Application.Autopilot;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Autopilot;

public sealed class SqliteHumanGateStore : IHumanGateStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteHumanGateStore(SqliteConnectionFactory factory, int writeQueueCapacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, writeQueueCapacity);
    }

    public ValueTask<HumanGateCreateResult> CreateAsync(HumanGateDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft, createdAtUtc);
        return _queue.ExecuteInTransactionAsync((connection, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(connection, tx, draft.RequestId);
            if (existing is not null)
            {
                if (Matches(existing, draft)) return HumanGateCreateResult.AlreadyExists;
                throw Conflict(draft.RequestId, "Gate request ID was reused with different immutable content.");
            }
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO human_gate_requests(
                  request_id, workflow_id, workflow_version, step_id, job_id, prompt, schema_version,
                  expires_at_utc, status, claimed_by, claim_until_utc, decision, decision_note,
                  decided_by, decided_at_utc, resume_message_id, created_at_utc)
                VALUES($requestId,$workflowId,$workflowVersion,$stepId,$jobId,$prompt,$schemaVersion,
                  $expiresAt,'OPEN',NULL,NULL,NULL,NULL,NULL,NULL,NULL,$createdAt);
                """;
            command.Parameters.AddWithValue("$requestId", draft.RequestId.ToString("D"));
            command.Parameters.AddWithValue("$workflowId", draft.WorkflowId);
            command.Parameters.AddWithValue("$workflowVersion", draft.WorkflowVersion);
            command.Parameters.AddWithValue("$stepId", draft.StepId);
            command.Parameters.AddWithValue("$jobId", draft.JobId.ToString("D"));
            command.Parameters.AddWithValue("$prompt", draft.Prompt);
            command.Parameters.AddWithValue("$schemaVersion", draft.SchemaVersion);
            command.Parameters.AddWithValue("$expiresAt", Text(draft.ExpiresAtUtc));
            command.Parameters.AddWithValue("$createdAt", Text(createdAtUtc));
            command.ExecuteNonQuery();
            return HumanGateCreateResult.Inserted;
        }, cancellationToken);
    }

    public async ValueTask<HumanGateRequest?> GetAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _factory.OpenConnection();
        return await Task.FromResult(Read(connection, null, requestId)).ConfigureAwait(false);
    }

    public ValueTask<HumanGateRequest> ClaimAsync(Guid requestId, string actorId, TimeSpan leaseDuration, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        ValidateActor(actorId);
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        return _queue.ExecuteInTransactionAsync((connection, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var current = Require(connection, tx, requestId);
            if (current.ExpiresAtUtc <= nowUtc) { Expire(connection, tx, requestId); return Require(connection, tx, requestId); }
            if (current.Status is HumanGateStatus.Approved or HumanGateStatus.Rejected or HumanGateStatus.Expired or HumanGateStatus.Cancelled)
                throw Conflict(requestId, "Terminal gate cannot be claimed.");
            if (current.Status == HumanGateStatus.Claimed && current.ClaimUntilUtc > nowUtc && !string.Equals(current.ClaimedBy, actorId, StringComparison.Ordinal))
                throw new HumanGateLeaseException(requestId, actorId);
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "UPDATE human_gate_requests SET status='CLAIMED', claimed_by=$actor, claim_until_utc=$until WHERE request_id=$id;";
            command.Parameters.AddWithValue("$actor", actorId);
            command.Parameters.AddWithValue("$until", Text(nowUtc.Add(leaseDuration)));
            command.Parameters.AddWithValue("$id", requestId.ToString("D"));
            command.ExecuteNonQuery();
            return Require(connection, tx, requestId);
        }, cancellationToken);
    }

    public ValueTask<HumanGateDecisionResult> DecideAsync(Guid requestId, string actorId, HumanGateDecision decision, string note, DateTimeOffset decidedAtUtc, CancellationToken cancellationToken = default)
    {
        ValidateActor(actorId);
        if (note is null || note.Length > 4096) throw new ArgumentException("Decision note is invalid.", nameof(note));
        return _queue.ExecuteInTransactionAsync((connection, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var current = Require(connection, tx, requestId);
            var target = decision == HumanGateDecision.Approve ? HumanGateStatus.Approved : HumanGateStatus.Rejected;
            if (current.Status is HumanGateStatus.Approved or HumanGateStatus.Rejected)
            {
                if (current.Status == target && string.Equals(current.DecidedBy, actorId, StringComparison.Ordinal) && string.Equals(current.DecisionNote, note, StringComparison.Ordinal))
                    return new HumanGateDecisionResult(current, true);
                throw Conflict(requestId, "Gate already has a different terminal decision.");
            }
            if (current.ExpiresAtUtc <= decidedAtUtc) { Expire(connection, tx, requestId); throw Conflict(requestId, "Gate expired before decision."); }
            if (current.Status != HumanGateStatus.Claimed || current.ClaimUntilUtc <= decidedAtUtc || !string.Equals(current.ClaimedBy, actorId, StringComparison.Ordinal))
                throw new HumanGateLeaseException(requestId, actorId);

            var resumeId = DeterministicResumeId(requestId);
            using (var outbox = connection.CreateCommand())
            {
                outbox.Transaction = tx;
                outbox.CommandText = """
                    INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc)
                    VALUES($messageId,'autopilot.human-gate.resolved','1.0.0',$payload,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);
                    """;
                outbox.Parameters.AddWithValue("$messageId", resumeId.ToString("D"));
                outbox.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new { requestId, current.JobId, decision = decision.ToString().ToUpperInvariant(), actorId }));
                outbox.Parameters.AddWithValue("$at", Text(decidedAtUtc));
                outbox.ExecuteNonQuery();
            }
            using (var update = connection.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                    UPDATE human_gate_requests SET status=$status, decision=$decision, decision_note=$note,
                    decided_by=$actor, decided_at_utc=$at, resume_message_id=$resume,
                    claimed_by=NULL, claim_until_utc=NULL WHERE request_id=$id;
                    """;
                update.Parameters.AddWithValue("$status", target == HumanGateStatus.Approved ? "APPROVED" : "REJECTED");
                update.Parameters.AddWithValue("$decision", decision == HumanGateDecision.Approve ? "APPROVE" : "REJECT");
                update.Parameters.AddWithValue("$note", note);
                update.Parameters.AddWithValue("$actor", actorId);
                update.Parameters.AddWithValue("$at", Text(decidedAtUtc));
                update.Parameters.AddWithValue("$resume", resumeId.ToString("D"));
                update.Parameters.AddWithValue("$id", requestId.ToString("D"));
                update.ExecuteNonQuery();
            }
            return new HumanGateDecisionResult(Require(connection, tx, requestId), false);
        }, cancellationToken);
    }

    public ValueTask<HumanGateRequest> CancelAsync(Guid requestId, string actorId, DateTimeOffset cancelledAtUtc, CancellationToken cancellationToken = default)
    {
        ValidateActor(actorId);
        return _queue.ExecuteInTransactionAsync((connection, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var current = Require(connection, tx, requestId);
            if (current.Status == HumanGateStatus.Cancelled) return current;
            if (current.Status is HumanGateStatus.Approved or HumanGateStatus.Rejected or HumanGateStatus.Expired)
                throw Conflict(requestId, "Terminal gate cannot be cancelled.");
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "UPDATE human_gate_requests SET status='CANCELLED', claimed_by=NULL, claim_until_utc=NULL, decided_by=$actor, decided_at_utc=$at WHERE request_id=$id;";
            command.Parameters.AddWithValue("$actor", actorId);
            command.Parameters.AddWithValue("$at", Text(cancelledAtUtc));
            command.Parameters.AddWithValue("$id", requestId.ToString("D"));
            command.ExecuteNonQuery();
            return Require(connection, tx, requestId);
        }, cancellationToken);
    }

    public ValueTask<int> ExpireDueAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
        _queue.ExecuteInTransactionAsync((connection, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "UPDATE human_gate_requests SET status='EXPIRED', claimed_by=NULL, claim_until_utc=NULL WHERE status IN ('OPEN','CLAIMED') AND expires_at_utc <= $now;";
            command.Parameters.AddWithValue("$now", Text(nowUtc));
            return command.ExecuteNonQuery();
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false);
    }

    private static HumanGateRequest Require(SqliteConnection c, SqliteTransaction tx, Guid id) => Read(c, tx, id) ?? throw new KeyNotFoundException($"Gate '{id:D}' was not found.");
    private static HumanGateRequest? Read(SqliteConnection c, SqliteTransaction? tx, Guid id)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT request_id,workflow_id,workflow_version,step_id,job_id,prompt,schema_version,expires_at_utc,status,claimed_by,claim_until_utc,decision,decision_note,decided_by,decided_at_utc,resume_message_id,created_at_utc FROM human_gate_requests WHERE request_id=$id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
        return new HumanGateRequest(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),r.GetString(3),Guid.Parse(r.GetString(4)),r.GetString(5),r.GetString(6),ParseTime(r.GetString(7)),ParseStatus(r.GetString(8)),NullableString(r,9),NullableTime(r,10),ParseDecision(NullableString(r,11)),NullableString(r,12),NullableString(r,13),NullableTime(r,14),NullableGuid(r,15),ParseTime(r.GetString(16)));
    }
    private static void Expire(SqliteConnection c, SqliteTransaction tx, Guid id) { using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="UPDATE human_gate_requests SET status='EXPIRED', claimed_by=NULL, claim_until_utc=NULL WHERE request_id=$id;"; cmd.Parameters.AddWithValue("$id",id.ToString("D")); cmd.ExecuteNonQuery(); }
    private static bool Matches(HumanGateRequest r, HumanGateDraft d) => r.RequestId==d.RequestId && r.WorkflowId==d.WorkflowId && r.WorkflowVersion==d.WorkflowVersion && r.StepId==d.StepId && r.JobId==d.JobId && r.Prompt==d.Prompt && r.SchemaVersion==d.SchemaVersion && r.ExpiresAtUtc==d.ExpiresAtUtc;
    private static void ValidateDraft(HumanGateDraft d, DateTimeOffset created) { if(d is null||d.RequestId==Guid.Empty||d.JobId==Guid.Empty||string.IsNullOrWhiteSpace(d.WorkflowId)||string.IsNullOrWhiteSpace(d.WorkflowVersion)||string.IsNullOrWhiteSpace(d.StepId)||string.IsNullOrWhiteSpace(d.Prompt)||d.Prompt.Length>16384||string.IsNullOrWhiteSpace(d.SchemaVersion)||d.ExpiresAtUtc<=created) throw new ArgumentException("Human gate draft is invalid."); }
    private static void ValidateActor(string actor) { if(string.IsNullOrWhiteSpace(actor)||actor.Length>256||actor.Any(char.IsControl)) throw new ArgumentException("Actor is invalid.",nameof(actor)); }
    private static Guid DeterministicResumeId(Guid id) { var bytes=id.ToByteArray(); bytes[0]^=0x5A; bytes[15]^=0xA5; return new Guid(bytes); }
    private static HumanGateConflictException Conflict(Guid id,string message)=>new(id,message);
    private static string Text(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string value)=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
    private static string? NullableString(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetString(i);
    private static DateTimeOffset? NullableTime(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:ParseTime(r.GetString(i));
    private static Guid? NullableGuid(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:Guid.Parse(r.GetString(i));
    private static HumanGateStatus ParseStatus(string value)=>value switch{"OPEN"=>HumanGateStatus.Open,"CLAIMED"=>HumanGateStatus.Claimed,"APPROVED"=>HumanGateStatus.Approved,"REJECTED"=>HumanGateStatus.Rejected,"EXPIRED"=>HumanGateStatus.Expired,"CANCELLED"=>HumanGateStatus.Cancelled,_=>throw new InvalidOperationException("Unknown gate status.")};
    private static HumanGateDecision? ParseDecision(string? value)=>value switch{null=>null,"APPROVE"=>HumanGateDecision.Approve,"REJECT"=>HumanGateDecision.Reject,_=>throw new InvalidOperationException("Unknown gate decision.")};
}

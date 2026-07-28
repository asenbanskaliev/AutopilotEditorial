using System.Globalization;
using System.Text.Json;
using BookStudio.Application.Autopilot;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Autopilot;

public sealed class SqliteDeadLetterStore : IDeadLetterStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteDeadLetterStore(SqliteConnectionFactory factory, int writeQueueCapacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, writeQueueCapacity);
    }

    public ValueTask<DeadLetterCaptureResult> CaptureAsync(DeadLetterDraft draft, DateTimeOffset capturedAtUtc, CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);
        return _queue.ExecuteInTransactionAsync((connection, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var existing = Read(connection, tx, draft.DeadLetterId);
            if (existing is not null)
            {
                if (Matches(existing, draft)) return new DeadLetterCaptureResult(existing, true);
                throw Conflict(draft.DeadLetterId, "Dead-letter ID was reused with different immutable failure content.");
            }

            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO dead_letters(dead_letter_id,source_kind,source_id,event_type,original_schema_version,
                  original_payload_json,attempt_count,failure_class,error,failure_fingerprint,status,
                  replacement_schema_version,replacement_payload_json,last_actor,last_reason,recovery_message_id,
                  captured_at_utc,updated_at_utc)
                VALUES($id,$kind,$source,$type,$schema,$payload,$attempts,$class,$error,$fingerprint,'QUARANTINED',
                  NULL,NULL,NULL,NULL,NULL,$at,$at);
                """;
            cmd.Parameters.AddWithValue("$id", draft.DeadLetterId.ToString("D"));
            cmd.Parameters.AddWithValue("$kind", SourceText(draft.SourceKind));
            cmd.Parameters.AddWithValue("$source", draft.SourceId.ToString("D"));
            cmd.Parameters.AddWithValue("$type", draft.EventType);
            cmd.Parameters.AddWithValue("$schema", draft.SchemaVersion);
            cmd.Parameters.AddWithValue("$payload", draft.PayloadJson);
            cmd.Parameters.AddWithValue("$attempts", draft.AttemptCount);
            cmd.Parameters.AddWithValue("$class", FailureText(draft.FailureClass));
            cmd.Parameters.AddWithValue("$error", draft.Error);
            cmd.Parameters.AddWithValue("$fingerprint", draft.FailureFingerprint);
            cmd.Parameters.AddWithValue("$at", Text(capturedAtUtc));
            cmd.ExecuteNonQuery();
            return new DeadLetterCaptureResult(Require(connection, tx, draft.DeadLetterId), false);
        }, cancellationToken);
    }

    public ValueTask<DeadLetterRepairResult> RepairAsync(DeadLetterRepairCommand command, DateTimeOffset repairedAtUtc, CancellationToken cancellationToken = default)
    {
        ValidateRepair(command);
        return _queue.ExecuteInTransactionAsync((connection, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var replay = ReadRequest(connection, tx, command.RequestId);
            if (replay is not null)
            {
                RequireSame(replay, command.DeadLetterId, "REPAIR", command.Actor, command.Reason, command.RequestFingerprint);
                return new DeadLetterRepairResult(Require(connection, tx, command.DeadLetterId), true);
            }
            var current = Require(connection, tx, command.DeadLetterId);
            if (current.Status != DeadLetterStatus.Quarantined) throw Transition(current, "repair");
            using (var update = connection.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = "UPDATE dead_letters SET status='READY_FOR_RETRY',replacement_schema_version=$schema,replacement_payload_json=$payload,last_actor=$actor,last_reason=$reason,updated_at_utc=$at WHERE dead_letter_id=$id;";
                update.Parameters.AddWithValue("$schema", command.ReplacementSchemaVersion);
                update.Parameters.AddWithValue("$payload", command.ReplacementPayloadJson);
                update.Parameters.AddWithValue("$actor", command.Actor);
                update.Parameters.AddWithValue("$reason", command.Reason);
                update.Parameters.AddWithValue("$at", Text(repairedAtUtc));
                update.Parameters.AddWithValue("$id", command.DeadLetterId.ToString("D"));
                update.ExecuteNonQuery();
            }
            InsertRequest(connection, tx, command.RequestId, command.DeadLetterId, "REPAIR", command.Actor, command.Reason, command.RequestFingerprint, "READY_FOR_RETRY", null, repairedAtUtc);
            return new DeadLetterRepairResult(Require(connection, tx, command.DeadLetterId), false);
        }, cancellationToken);
    }

    public ValueTask<DeadLetterRecoveryResult> RequeueAsync(DeadLetterRecoveryCommand command, DateTimeOffset requeuedAtUtc, CancellationToken cancellationToken = default)
    {
        ValidateControl(command.RequestId, command.DeadLetterId, command.Actor, command.Reason, command.RequestFingerprint);
        return _queue.ExecuteInTransactionAsync((connection, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var replay = ReadRequest(connection, tx, command.RequestId);
            if (replay is not null)
            {
                RequireSame(replay, command.DeadLetterId, "REQUEUE", command.Actor, command.Reason, command.RequestFingerprint);
                var record = Require(connection, tx, command.DeadLetterId);
                return new DeadLetterRecoveryResult(record, true, replay.RecoveryMessageId ?? throw new InvalidOperationException("Recovery receipt is missing message identity."));
            }
            var current = Require(connection, tx, command.DeadLetterId);
            if (current.Status != DeadLetterStatus.ReadyForRetry) throw Transition(current, "requeue");
            var messageId = DeterministicRecoveryId(command.DeadLetterId);
            using (var outbox = connection.CreateCommand())
            {
                outbox.Transaction = tx;
                outbox.CommandText = """
                    INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc)
                    VALUES($messageId,'autopilot.dead-letter.requeued','1.0.0',$payload,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);
                    """;
                outbox.Parameters.AddWithValue("$messageId", messageId.ToString("D"));
                outbox.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new { command.DeadLetterId, current.SourceKind, current.SourceId, current.EventType, schemaVersion = current.ReplacementSchemaVersion, payloadJson = current.ReplacementPayloadJson, command.Actor }));
                outbox.Parameters.AddWithValue("$at", Text(requeuedAtUtc));
                outbox.ExecuteNonQuery();
            }
            using (var update = connection.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = "UPDATE dead_letters SET status='REQUEUED',last_actor=$actor,last_reason=$reason,recovery_message_id=$message,updated_at_utc=$at WHERE dead_letter_id=$id;";
                update.Parameters.AddWithValue("$actor", command.Actor);
                update.Parameters.AddWithValue("$reason", command.Reason);
                update.Parameters.AddWithValue("$message", messageId.ToString("D"));
                update.Parameters.AddWithValue("$at", Text(requeuedAtUtc));
                update.Parameters.AddWithValue("$id", command.DeadLetterId.ToString("D"));
                update.ExecuteNonQuery();
            }
            InsertRequest(connection, tx, command.RequestId, command.DeadLetterId, "REQUEUE", command.Actor, command.Reason, command.RequestFingerprint, "REQUEUED", messageId, requeuedAtUtc);
            return new DeadLetterRecoveryResult(Require(connection, tx, command.DeadLetterId), false, messageId);
        }, cancellationToken);
    }

    public ValueTask<DeadLetterRecord> DiscardAsync(DeadLetterDiscardCommand command, DateTimeOffset discardedAtUtc, CancellationToken cancellationToken = default)
    {
        ValidateControl(command.RequestId, command.DeadLetterId, command.Actor, command.Reason, command.RequestFingerprint);
        return _queue.ExecuteInTransactionAsync((connection, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var replay = ReadRequest(connection, tx, command.RequestId);
            if (replay is not null)
            {
                RequireSame(replay, command.DeadLetterId, "DISCARD", command.Actor, command.Reason, command.RequestFingerprint);
                return Require(connection, tx, command.DeadLetterId);
            }
            var current = Require(connection, tx, command.DeadLetterId);
            if (current.Status is not (DeadLetterStatus.Quarantined or DeadLetterStatus.ReadyForRetry)) throw Transition(current, "discard");
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE dead_letters SET status='DISCARDED',last_actor=$actor,last_reason=$reason,recovery_message_id=NULL,updated_at_utc=$at WHERE dead_letter_id=$id;";
            update.Parameters.AddWithValue("$actor", command.Actor);
            update.Parameters.AddWithValue("$reason", command.Reason);
            update.Parameters.AddWithValue("$at", Text(discardedAtUtc));
            update.Parameters.AddWithValue("$id", command.DeadLetterId.ToString("D"));
            update.ExecuteNonQuery();
            InsertRequest(connection, tx, command.RequestId, command.DeadLetterId, "DISCARD", command.Actor, command.Reason, command.RequestFingerprint, "DISCARDED", null, discardedAtUtc);
            return Require(connection, tx, command.DeadLetterId);
        }, cancellationToken);
    }

    public async ValueTask<DeadLetterRecord?> GetAsync(Guid deadLetterId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _factory.OpenConnection();
        return await Task.FromResult(Read(connection, null, deadLetterId)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false);
    }

    private static DeadLetterRecord Require(SqliteConnection c, SqliteTransaction tx, Guid id) => Read(c, tx, id) ?? throw new KeyNotFoundException($"Dead letter '{id:D}' was not found.");
    private static DeadLetterRecord? Read(SqliteConnection c, SqliteTransaction? tx, Guid id)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT dead_letter_id,source_kind,source_id,event_type,original_schema_version,original_payload_json,attempt_count,failure_class,error,failure_fingerprint,status,replacement_schema_version,replacement_payload_json,last_actor,last_reason,recovery_message_id,captured_at_utc,updated_at_utc FROM dead_letters WHERE dead_letter_id=$id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
        return new DeadLetterRecord(Guid.Parse(r.GetString(0)), ParseSource(r.GetString(1)), Guid.Parse(r.GetString(2)), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6), ParseFailure(r.GetString(7)), r.GetString(8), r.GetString(9), ParseStatus(r.GetString(10)), NullableString(r,11), NullableString(r,12), NullableString(r,13), NullableString(r,14), NullableGuid(r,15), ParseTime(r.GetString(16)), ParseTime(r.GetString(17)));
    }

    private sealed record RequestRow(Guid DeadLetterId, string Operation, string Actor, string Reason, string Fingerprint, Guid? RecoveryMessageId);
    private static RequestRow? ReadRequest(SqliteConnection c, SqliteTransaction tx, Guid requestId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT dead_letter_id,operation,actor,reason,request_fingerprint,recovery_message_id FROM dead_letter_requests WHERE request_id=$id;";
        cmd.Parameters.AddWithValue("$id", requestId.ToString("D"));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new RequestRow(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), NullableGuid(r,5)) : null;
    }

    private static void InsertRequest(SqliteConnection c, SqliteTransaction tx, Guid requestId, Guid deadLetterId, string operation, string actor, string reason, string fingerprint, string status, Guid? messageId, DateTimeOffset at)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO dead_letter_requests(request_id,dead_letter_id,operation,actor,reason,request_fingerprint,result_status,recovery_message_id,created_at_utc) VALUES($request,$dead,$operation,$actor,$reason,$fingerprint,$status,$message,$at);";
        cmd.Parameters.AddWithValue("$request", requestId.ToString("D"));
        cmd.Parameters.AddWithValue("$dead", deadLetterId.ToString("D"));
        cmd.Parameters.AddWithValue("$operation", operation);
        cmd.Parameters.AddWithValue("$actor", actor);
        cmd.Parameters.AddWithValue("$reason", reason);
        cmd.Parameters.AddWithValue("$fingerprint", fingerprint);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$message", messageId is null ? DBNull.Value : messageId.Value.ToString("D"));
        cmd.Parameters.AddWithValue("$at", Text(at));
        cmd.ExecuteNonQuery();
    }

    private static void RequireSame(RequestRow row, Guid deadLetterId, string operation, string actor, string reason, string fingerprint)
    {
        if (row.DeadLetterId != deadLetterId || row.Operation != operation || row.Actor != actor || row.Reason != reason || row.Fingerprint != fingerprint)
            throw Conflict(deadLetterId, "Request ID was reused with different immutable recovery content.");
    }

    private static bool Matches(DeadLetterRecord r, DeadLetterDraft d) => r.DeadLetterId == d.DeadLetterId && r.SourceKind == d.SourceKind && r.SourceId == d.SourceId && r.EventType == d.EventType && r.OriginalSchemaVersion == d.SchemaVersion && r.OriginalPayloadJson == d.PayloadJson && r.AttemptCount == d.AttemptCount && r.FailureClass == d.FailureClass && r.Error == d.Error && r.FailureFingerprint == d.FailureFingerprint;
    private static void ValidateDraft(DeadLetterDraft d) { if (d is null || d.DeadLetterId == Guid.Empty || d.SourceId == Guid.Empty || string.IsNullOrWhiteSpace(d.EventType) || string.IsNullOrWhiteSpace(d.SchemaVersion) || string.IsNullOrWhiteSpace(d.PayloadJson) || d.AttemptCount < 0 || string.IsNullOrWhiteSpace(d.Error) || string.IsNullOrWhiteSpace(d.FailureFingerprint)) throw new ArgumentException("Dead-letter draft is invalid."); }
    private static void ValidateRepair(DeadLetterRepairCommand c) { ValidateControl(c.RequestId,c.DeadLetterId,c.Actor,c.Reason,c.RequestFingerprint); if(string.IsNullOrWhiteSpace(c.ReplacementPayloadJson)||string.IsNullOrWhiteSpace(c.ReplacementSchemaVersion)) throw new ArgumentException("Repair payload is invalid."); }
    private static void ValidateControl(Guid requestId, Guid deadLetterId, string actor, string reason, string fingerprint) { if(requestId==Guid.Empty||deadLetterId==Guid.Empty||string.IsNullOrWhiteSpace(actor)||actor.Length>256||string.IsNullOrWhiteSpace(reason)||reason.Length>4096||string.IsNullOrWhiteSpace(fingerprint)) throw new ArgumentException("Dead-letter command is invalid."); }
    private static Guid DeterministicRecoveryId(Guid id) { var bytes=id.ToByteArray(); bytes[1]^=0x6D; bytes[14]^=0xD6; return new Guid(bytes); }
    private static DeadLetterConflictException Conflict(Guid id,string message)=>new(id,message);
    private static DeadLetterTransitionException Transition(DeadLetterRecord r,string operation)=>new(r.DeadLetterId,r.Status,operation);
    private static string Text(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string value)=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
    private static string? NullableString(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetString(i);
    private static Guid? NullableGuid(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:Guid.Parse(r.GetString(i));
    private static string SourceText(DeadLetterSourceKind value)=>value==DeadLetterSourceKind.SchedulerJob?"SCHEDULER_JOB":"OUTBOX_MESSAGE";
    private static DeadLetterSourceKind ParseSource(string value)=>value switch{"SCHEDULER_JOB"=>DeadLetterSourceKind.SchedulerJob,"OUTBOX_MESSAGE"=>DeadLetterSourceKind.OutboxMessage,_=>throw new InvalidOperationException("Unknown dead-letter source.")};
    private static string FailureText(DeadLetterFailureClass value)=>value switch{DeadLetterFailureClass.TransientExhausted=>"TRANSIENT_EXHAUSTED",DeadLetterFailureClass.Permanent=>"PERMANENT",DeadLetterFailureClass.ContractViolation=>"CONTRACT_VIOLATION",DeadLetterFailureClass.SecurityViolation=>"SECURITY_VIOLATION",_=>"UNKNOWN"};
    private static DeadLetterFailureClass ParseFailure(string value)=>value switch{"TRANSIENT_EXHAUSTED"=>DeadLetterFailureClass.TransientExhausted,"PERMANENT"=>DeadLetterFailureClass.Permanent,"CONTRACT_VIOLATION"=>DeadLetterFailureClass.ContractViolation,"SECURITY_VIOLATION"=>DeadLetterFailureClass.SecurityViolation,"UNKNOWN"=>DeadLetterFailureClass.Unknown,_=>throw new InvalidOperationException("Unknown dead-letter failure class.")};
    private static DeadLetterStatus ParseStatus(string value)=>value switch{"QUARANTINED"=>DeadLetterStatus.Quarantined,"READY_FOR_RETRY"=>DeadLetterStatus.ReadyForRetry,"REQUEUED"=>DeadLetterStatus.Requeued,"DISCARDED"=>DeadLetterStatus.Discarded,_=>throw new InvalidOperationException("Unknown dead-letter status.")};
}

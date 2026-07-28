using System.Globalization;
using System.Text.Json;
using BookStudio.Application.Autopilot;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Autopilot;

public sealed class SqliteExecutionControlStore : IExecutionControlStore, IAsyncDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWriteQueue _queue;
    private int _disposed;

    public SqliteExecutionControlStore(SqliteConnectionFactory factory, int writeQueueCapacity = 64)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _queue = new SqliteWriteQueue(factory, writeQueueCapacity);
    }

    public ValueTask<ExecutionControlResult> ApplyAsync(ExecutionControlCommand command, DateTimeOffset appliedAtUtc, CancellationToken cancellationToken = default)
    {
        Validate(command);
        return _queue.ExecuteInTransactionAsync((connection, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            var replay = ReadReceipt(connection, tx, command.RequestId);
            if (replay is not null)
            {
                if (!Matches(replay.Value, command)) throw new ExecutionControlConflictException("Control request ID was reused with different content.");
                var execution = Require(connection, tx, command.ExecutionId);
                return new ExecutionControlResult(execution, true, replay.Value.MessageId);
            }

            var current = Read(connection, tx, command.ExecutionId) ?? InsertInitial(connection, tx, command.ExecutionId, appliedAtUtc);
            var target = Transition(current.Status, command.Action);
            var version = checked(current.Version + 1);
            var messageId = DeterministicMessageId(command.RequestId);

            using (var update = connection.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = "UPDATE controlled_executions SET status=$status,version=$version,last_actor=$actor,last_reason=$reason,active_job_id=CASE WHEN $status='CANCELLED' THEN NULL ELSE active_job_id END,updated_at_utc=$at WHERE execution_id=$id;";
                update.Parameters.AddWithValue("$status", StatusText(target));
                update.Parameters.AddWithValue("$version", version);
                update.Parameters.AddWithValue("$actor", command.Actor);
                update.Parameters.AddWithValue("$reason", command.Reason);
                update.Parameters.AddWithValue("$at", Text(appliedAtUtc));
                update.Parameters.AddWithValue("$id", command.ExecutionId.ToString("D"));
                update.ExecuteNonQuery();
            }

            using (var receipt = connection.CreateCommand())
            {
                receipt.Transaction = tx;
                receipt.CommandText = "INSERT INTO execution_control_receipts(request_id,execution_id,action,actor,reason,request_fingerprint,resulting_status,resulting_version,control_message_id,applied_at_utc) VALUES($request,$execution,$action,$actor,$reason,$fingerprint,$status,$version,$message,$at);";
                receipt.Parameters.AddWithValue("$request", command.RequestId.ToString("D"));
                receipt.Parameters.AddWithValue("$execution", command.ExecutionId.ToString("D"));
                receipt.Parameters.AddWithValue("$action", ActionText(command.Action));
                receipt.Parameters.AddWithValue("$actor", command.Actor);
                receipt.Parameters.AddWithValue("$reason", command.Reason);
                receipt.Parameters.AddWithValue("$fingerprint", command.RequestFingerprint);
                receipt.Parameters.AddWithValue("$status", StatusText(target));
                receipt.Parameters.AddWithValue("$version", version);
                receipt.Parameters.AddWithValue("$message", messageId.ToString("D"));
                receipt.Parameters.AddWithValue("$at", Text(appliedAtUtc));
                receipt.ExecuteNonQuery();
            }

            using (var outbox = connection.CreateCommand())
            {
                outbox.Transaction = tx;
                outbox.CommandText = "INSERT INTO outbox_messages(message_id,event_type,schema_version,payload_json,occurred_at_utc,available_at_utc,status,attempts,locked_by,locked_until_utc,last_error,processed_at_utc,created_at_utc) VALUES($id,'autopilot.execution.controlled','1.0.0',$payload,$at,$at,'PENDING',0,NULL,NULL,NULL,NULL,$at);";
                outbox.Parameters.AddWithValue("$id", messageId.ToString("D"));
                outbox.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new { command.ExecutionId, action = ActionText(command.Action), status = StatusText(target), version, command.Actor, command.Reason }));
                outbox.Parameters.AddWithValue("$at", Text(appliedAtUtc));
                outbox.ExecuteNonQuery();
            }

            return new ExecutionControlResult(Require(connection, tx, command.ExecutionId), false, messageId);
        }, cancellationToken);
    }

    public async ValueTask<ControlledExecution?> GetAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _factory.OpenConnection();
        return await Task.FromResult(Read(connection, null, executionId)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) await _queue.DisposeAsync().ConfigureAwait(false);
    }

    private static ExecutionControlStatus Transition(ExecutionControlStatus status, ExecutionControlAction action) => (status, action) switch
    {
        (ExecutionControlStatus.Runnable, ExecutionControlAction.Pause) => ExecutionControlStatus.Paused,
        (ExecutionControlStatus.Running, ExecutionControlAction.Pause) => ExecutionControlStatus.Paused,
        (ExecutionControlStatus.Paused, ExecutionControlAction.Resume) => ExecutionControlStatus.Runnable,
        (ExecutionControlStatus.Runnable, ExecutionControlAction.Cancel) => ExecutionControlStatus.Cancelled,
        (ExecutionControlStatus.Running, ExecutionControlAction.Cancel) => ExecutionControlStatus.Cancelled,
        (ExecutionControlStatus.Paused, ExecutionControlAction.Cancel) => ExecutionControlStatus.Cancelled,
        _ => throw new ExecutionControlTransitionException(status, action),
    };

    private static ControlledExecution InsertInitial(SqliteConnection c, SqliteTransaction tx, Guid id, DateTimeOffset at)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO controlled_executions(execution_id,status,version,last_actor,last_reason,active_job_id,updated_at_utc) VALUES($id,'RUNNABLE',0,NULL,NULL,NULL,$at);";
        cmd.Parameters.AddWithValue("$id", id.ToString("D")); cmd.Parameters.AddWithValue("$at", Text(at)); cmd.ExecuteNonQuery();
        return Require(c, tx, id);
    }

    private static ControlledExecution Require(SqliteConnection c, SqliteTransaction tx, Guid id) => Read(c, tx, id) ?? throw new KeyNotFoundException($"Execution '{id:D}' not found.");
    private static ControlledExecution? Read(SqliteConnection c, SqliteTransaction? tx, Guid id)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT execution_id,status,version,last_actor,last_reason,updated_at_utc,active_job_id FROM controlled_executions WHERE execution_id=$id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D")); using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
        return new ControlledExecution(Guid.Parse(r.GetString(0)), ParseStatus(r.GetString(1)), r.GetInt64(2), r.IsDBNull(3)?null:r.GetString(3), r.IsDBNull(4)?null:r.GetString(4), ParseTime(r.GetString(5)), r.IsDBNull(6)?null:Guid.Parse(r.GetString(6)));
    }

    private static (Guid ExecutionId,string Action,string Actor,string Reason,string Fingerprint,Guid MessageId)? ReadReceipt(SqliteConnection c, SqliteTransaction tx, Guid requestId)
    {
        using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="SELECT execution_id,action,actor,reason,request_fingerprint,control_message_id FROM execution_control_receipts WHERE request_id=$id;"; cmd.Parameters.AddWithValue("$id",requestId.ToString("D")); using var r=cmd.ExecuteReader();
        return r.Read() ? (Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),Guid.Parse(r.GetString(5))) : null;
    }

    private static bool Matches((Guid ExecutionId,string Action,string Actor,string Reason,string Fingerprint,Guid MessageId) r, ExecutionControlCommand c) => r.ExecutionId==c.ExecutionId && r.Action==ActionText(c.Action) && r.Actor==c.Actor && r.Reason==c.Reason && r.Fingerprint==c.RequestFingerprint;
    private static void Validate(ExecutionControlCommand c) { if(c is null||c.RequestId==Guid.Empty||c.ExecutionId==Guid.Empty||string.IsNullOrWhiteSpace(c.Actor)||c.Actor.Length>256||string.IsNullOrWhiteSpace(c.Reason)||c.Reason.Length>2048||string.IsNullOrWhiteSpace(c.RequestFingerprint)||c.RequestFingerprint.Length>256) throw new ArgumentException("Execution control command is invalid."); }
    private static Guid DeterministicMessageId(Guid id) { var b=id.ToByteArray(); b[0]^=0x3C; b[15]^=0xC3; return new Guid(b); }
    private static string ActionText(ExecutionControlAction a)=>a switch{ExecutionControlAction.Pause=>"PAUSE",ExecutionControlAction.Resume=>"RESUME",ExecutionControlAction.Cancel=>"CANCEL",_=>throw new ArgumentOutOfRangeException(nameof(a))};
    private static string StatusText(ExecutionControlStatus s)=>s switch{ExecutionControlStatus.Runnable=>"RUNNABLE",ExecutionControlStatus.Running=>"RUNNING",ExecutionControlStatus.Paused=>"PAUSED",ExecutionControlStatus.Cancelled=>"CANCELLED",_=>throw new ArgumentOutOfRangeException(nameof(s))};
    private static ExecutionControlStatus ParseStatus(string s)=>s switch{"RUNNABLE"=>ExecutionControlStatus.Runnable,"RUNNING"=>ExecutionControlStatus.Running,"PAUSED"=>ExecutionControlStatus.Paused,"CANCELLED"=>ExecutionControlStatus.Cancelled,_=>throw new InvalidOperationException("Unknown execution status.")};
    private static string Text(DateTimeOffset v)=>v.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string v)=>DateTimeOffset.Parse(v,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
}

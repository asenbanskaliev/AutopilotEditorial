using System.Globalization;
using System.Text.Json;
using BookStudio.Application.Autopilot;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Autopilot;

public sealed class SqliteJobSchedulerStore : IJobSchedulerStore, IAsyncDisposable
{
    private const int MaximumPayloadCharacters = 1_048_576;
    private const int MaximumErrorCharacters = 2_048;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteWriteQueue _writeQueue;
    private int _disposed;

    public SqliteJobSchedulerStore(SqliteConnectionFactory connectionFactory, int writeQueueCapacity = 64)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _writeQueue = new SqliteWriteQueue(connectionFactory, writeQueueCapacity);
    }

    public ValueTask<JobCreateResult> CreateAsync(JobDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ValidateDraft(draft);
        var availableAt = ToText(draft.AvailableAtUtc);
        var createdAt = ToText(createdAtUtc);
        return _writeQueue.ExecuteInTransactionAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = """
                    SELECT job_type, schema_version, payload_json, priority, available_at_utc
                    FROM scheduler_jobs WHERE job_id = $jobId;
                    """;
                existing.Parameters.AddWithValue("$jobId", draft.JobId.ToString("D"));
                using var reader = existing.ExecuteReader();
                if (reader.Read())
                {
                    var matches =
                        string.Equals(reader.GetString(0), draft.JobType, StringComparison.Ordinal) &&
                        string.Equals(reader.GetString(1), draft.SchemaVersion, StringComparison.Ordinal) &&
                        string.Equals(reader.GetString(2), draft.PayloadJson, StringComparison.Ordinal) &&
                        reader.GetInt32(3) == draft.Priority &&
                        string.Equals(reader.GetString(4), availableAt, StringComparison.Ordinal);
                    if (!matches) throw new JobConflictException(draft.JobId);
                    return JobCreateResult.AlreadyExists;
                }
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO scheduler_jobs(
                    job_id, job_type, schema_version, payload_json, priority,
                    available_at_utc, status, attempts, locked_by, locked_until_utc,
                    last_error, completed_at_utc, created_at_utc)
                VALUES(
                    $jobId, $jobType, $schemaVersion, $payloadJson, $priority,
                    $availableAtUtc, 'QUEUED', 0, NULL, NULL, NULL, NULL, $createdAtUtc);
                """;
            insert.Parameters.AddWithValue("$jobId", draft.JobId.ToString("D"));
            insert.Parameters.AddWithValue("$jobType", draft.JobType);
            insert.Parameters.AddWithValue("$schemaVersion", draft.SchemaVersion);
            insert.Parameters.AddWithValue("$payloadJson", draft.PayloadJson);
            insert.Parameters.AddWithValue("$priority", draft.Priority);
            insert.Parameters.AddWithValue("$availableAtUtc", availableAt);
            insert.Parameters.AddWithValue("$createdAtUtc", createdAt);
            insert.ExecuteNonQuery();
            return JobCreateResult.Inserted;
        }, cancellationToken);
    }

    public ValueTask<IReadOnlyList<ScheduledJob>> ClaimAsync(string workerId, int maximumJobs, TimeSpan leaseDuration, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ValidateWorker(workerId);
        if (maximumJobs is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumJobs));
        ValidateLease(leaseDuration);
        var now = ToText(nowUtc);
        var lockedUntil = ToText(nowUtc.ToUniversalTime().Add(leaseDuration));
        return _writeQueue.ExecuteInTransactionAsync<IReadOnlyList<ScheduledJob>>((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var ids = new List<Guid>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT job_id FROM scheduler_jobs
                    WHERE ((status IN ('QUEUED','FAILED') AND available_at_utc <= $nowUtc)
                           OR (status = 'RUNNING' AND locked_until_utc <= $nowUtc))
                    ORDER BY priority DESC, available_at_utc, created_at_utc, job_id
                    LIMIT $maximumJobs;
                    """;
                select.Parameters.AddWithValue("$nowUtc", now);
                select.Parameters.AddWithValue("$maximumJobs", maximumJobs);
                using var reader = select.ExecuteReader();
                while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0)));
            }

            var claimed = new List<ScheduledJob>(ids.Count);
            foreach (var id in ids)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE scheduler_jobs SET
                        status='RUNNING', attempts=attempts+1,
                        locked_by=$workerId, locked_until_utc=$lockedUntilUtc,
                        completed_at_utc=NULL
                    WHERE job_id=$jobId AND
                        (((status IN ('QUEUED','FAILED') AND available_at_utc <= $nowUtc)
                         OR (status='RUNNING' AND locked_until_utc <= $nowUtc)));
                    """;
                update.Parameters.AddWithValue("$workerId", workerId);
                update.Parameters.AddWithValue("$lockedUntilUtc", lockedUntil);
                update.Parameters.AddWithValue("$jobId", id.ToString("D"));
                update.Parameters.AddWithValue("$nowUtc", now);
                if (update.ExecuteNonQuery() == 1)
                    claimed.Add(ReadById(connection, transaction, id) ?? throw new InvalidOperationException("Claimed job disappeared."));
            }
            return claimed;
        }, cancellationToken);
    }

    public ValueTask RenewAsync(Guid jobId, string workerId, TimeSpan leaseDuration, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureActive(); ValidateJobId(jobId); ValidateWorker(workerId); ValidateLease(leaseDuration);
        var now = ToText(nowUtc); var lockedUntil = ToText(nowUtc.ToUniversalTime().Add(leaseDuration));
        return _writeQueue.ExecuteInTransactionAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE scheduler_jobs SET locked_until_utc=$lockedUntilUtc
                WHERE job_id=$jobId AND status='RUNNING' AND locked_by=$workerId AND locked_until_utc > $nowUtc;
                """;
            command.Parameters.AddWithValue("$lockedUntilUtc", lockedUntil);
            command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
            command.Parameters.AddWithValue("$workerId", workerId);
            command.Parameters.AddWithValue("$nowUtc", now);
            if (command.ExecuteNonQuery() != 1) throw new JobLeaseException(jobId, workerId);
            return true;
        }, cancellationToken).AsVoid();
    }

    public ValueTask CompleteAsync(Guid jobId, string workerId, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default) =>
        TransitionAsync(jobId, workerId, completedAtUtc, "COMPLETED", null, null, cancellationToken);

    public ValueTask FailAsync(Guid jobId, string workerId, string error, DateTimeOffset failedAtUtc, DateTimeOffset retryAtUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        if (retryAtUtc < failedAtUtc) throw new ArgumentOutOfRangeException(nameof(retryAtUtc));
        var bounded = error.Length <= MaximumErrorCharacters ? error : error[..MaximumErrorCharacters];
        return TransitionAsync(jobId, workerId, failedAtUtc, "FAILED", bounded, retryAtUtc, cancellationToken);
    }

    public ValueTask<ScheduledJob?> GetAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        EnsureActive(); ValidateJobId(jobId); cancellationToken.ThrowIfCancellationRequested();
        using var connection = _connectionFactory.OpenConnection();
        return ValueTask.FromResult(ReadById(connection, null, jobId));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await _writeQueue.DisposeAsync().ConfigureAwait(false);
    }

    private ValueTask TransitionAsync(Guid jobId, string workerId, DateTimeOffset atUtc, string status, string? error, DateTimeOffset? retryAtUtc, CancellationToken cancellationToken)
    {
        EnsureActive(); ValidateJobId(jobId); ValidateWorker(workerId);
        var at = ToText(atUtc); var retry = retryAtUtc is null ? null : ToText(retryAtUtc.Value);
        return _writeQueue.ExecuteInTransactionAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = status == "COMPLETED" ? """
                UPDATE scheduler_jobs SET status='COMPLETED', locked_by=NULL, locked_until_utc=NULL,
                    last_error=NULL, completed_at_utc=$atUtc
                WHERE job_id=$jobId AND status='RUNNING' AND locked_by=$workerId AND locked_until_utc > $atUtc;
                """ : """
                UPDATE scheduler_jobs SET status='FAILED', available_at_utc=$retryAtUtc,
                    locked_by=NULL, locked_until_utc=NULL, last_error=$lastError, completed_at_utc=NULL
                WHERE job_id=$jobId AND status='RUNNING' AND locked_by=$workerId AND locked_until_utc > $atUtc;
                """;
            command.Parameters.AddWithValue("$atUtc", at);
            command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
            command.Parameters.AddWithValue("$workerId", workerId);
            if (status != "COMPLETED")
            {
                command.Parameters.AddWithValue("$retryAtUtc", retry!);
                command.Parameters.AddWithValue("$lastError", error!);
            }
            if (command.ExecuteNonQuery() != 1) throw new JobLeaseException(jobId, workerId);
            return true;
        }, cancellationToken).AsVoid();
    }

    private static ScheduledJob? ReadById(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT job_id,job_type,schema_version,payload_json,priority,available_at_utc,status,attempts,
                   locked_by,locked_until_utc,last_error,completed_at_utc,created_at_utc
            FROM scheduler_jobs WHERE job_id=$jobId;
            """;
        command.Parameters.AddWithValue("$jobId", id.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new ScheduledJob(
            Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt32(4), Parse(reader.GetString(5)), ParseStatus(reader.GetString(6)), reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : Parse(reader.GetString(11)),
            Parse(reader.GetString(12))) : null;
    }

    private static void ValidateDraft(JobDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft); ValidateJobId(draft.JobId);
        ValidateToken(draft.JobType, nameof(draft.JobType), 256); ValidateToken(draft.SchemaVersion, nameof(draft.SchemaVersion), 64);
        if (draft.Priority is < -1000 or > 1000) throw new ArgumentOutOfRangeException(nameof(draft.Priority));
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.PayloadJson);
        if (draft.PayloadJson.Length > MaximumPayloadCharacters) throw new ArgumentOutOfRangeException(nameof(draft.PayloadJson));
        try { using var document = JsonDocument.Parse(draft.PayloadJson); } catch (JsonException ex) { throw new ArgumentException("Job payload must be valid JSON.", nameof(draft.PayloadJson), ex); }
    }

    private static void ValidateLease(TimeSpan value) { if (value <= TimeSpan.Zero || value > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(value)); }
    private static void ValidateWorker(string value) => ValidateToken(value, nameof(value), 128);
    private static void ValidateJobId(Guid id) { if (id == Guid.Empty) throw new ArgumentException("Job ID must not be empty.", nameof(id)); }
    private static void ValidateToken(string value, string name, int max) { ArgumentException.ThrowIfNullOrWhiteSpace(value, name); if (value.Length > max || value.Any(char.IsControl)) throw new ArgumentException($"{name} is invalid.", name); }
    private static string ToText(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    private static JobStatus ParseStatus(string value) => value switch { "QUEUED" => JobStatus.Queued, "RUNNING" => JobStatus.Running, "FAILED" => JobStatus.Failed, "COMPLETED" => JobStatus.Completed, _ => throw new InvalidOperationException($"Unknown job status: {value}") };
    private void EnsureActive() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

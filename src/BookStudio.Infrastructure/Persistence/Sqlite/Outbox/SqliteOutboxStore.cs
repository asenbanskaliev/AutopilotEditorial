using System.Globalization;
using System.Text.Json;
using BookStudio.Application.Outbox;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

/// <summary>SQLite at-least-once Outbox with ownership-safe leases.</summary>
public sealed class SqliteOutboxStore : IOutboxStore
{
    private const int MaximumPayloadCharacters = 1_048_576;
    private const int MaximumErrorCharacters = 2_048;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteWriteQueue _writeQueue;
    private int _disposed;

    public SqliteOutboxStore(
        SqliteConnectionFactory connectionFactory,
        int writeQueueCapacity = 64)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _writeQueue = new SqliteWriteQueue(connectionFactory, writeQueueCapacity);
    }

    public ValueTask<OutboxEnqueueResult> EnqueueAsync(
        OutboxMessageDraft draft,
        DateTimeOffset enqueuedAtUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ValidateDraft(draft);
        var occurredAt = ToText(draft.OccurredAtUtc);
        var availableAt = ToText(draft.AvailableAtUtc);
        var createdAt = ToText(enqueuedAtUtc);

        return _writeQueue.ExecuteInTransactionAsync(
            (connection, transaction, token) =>
            {
                token.ThrowIfCancellationRequested();
                using (var existing = connection.CreateCommand())
                {
                    existing.Transaction = transaction;
                    existing.CommandText = """
                        SELECT event_type, schema_version, payload_json, occurred_at_utc, available_at_utc
                        FROM outbox_messages
                        WHERE message_id = $messageId;
                        """;
                    existing.Parameters.AddWithValue("$messageId", draft.MessageId.ToString("D"));
                    using var reader = existing.ExecuteReader();
                    if (reader.Read())
                    {
                        var matches =
                            string.Equals(reader.GetString(0), draft.EventType, StringComparison.Ordinal) &&
                            string.Equals(reader.GetString(1), draft.SchemaVersion, StringComparison.Ordinal) &&
                            string.Equals(reader.GetString(2), draft.PayloadJson, StringComparison.Ordinal) &&
                            string.Equals(reader.GetString(3), occurredAt, StringComparison.Ordinal) &&
                            string.Equals(reader.GetString(4), availableAt, StringComparison.Ordinal);
                        if (!matches)
                        {
                            throw new OutboxMessageConflictException(draft.MessageId);
                        }
                        return OutboxEnqueueResult.AlreadyExists;
                    }
                }

                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO outbox_messages(
                        message_id, event_type, schema_version, payload_json,
                        occurred_at_utc, available_at_utc, status, attempts,
                        locked_by, locked_until_utc, last_error, processed_at_utc, created_at_utc)
                    VALUES(
                        $messageId, $eventType, $schemaVersion, $payloadJson,
                        $occurredAtUtc, $availableAtUtc, 'PENDING', 0,
                        NULL, NULL, NULL, NULL, $createdAtUtc);
                    """;
                insert.Parameters.AddWithValue("$messageId", draft.MessageId.ToString("D"));
                insert.Parameters.AddWithValue("$eventType", draft.EventType);
                insert.Parameters.AddWithValue("$schemaVersion", draft.SchemaVersion);
                insert.Parameters.AddWithValue("$payloadJson", draft.PayloadJson);
                insert.Parameters.AddWithValue("$occurredAtUtc", occurredAt);
                insert.Parameters.AddWithValue("$availableAtUtc", availableAt);
                insert.Parameters.AddWithValue("$createdAtUtc", createdAt);
                insert.ExecuteNonQuery();
                return OutboxEnqueueResult.Inserted;
            },
            cancellationToken);
    }

    public ValueTask<IReadOnlyList<OutboxMessage>> ClaimAsync(
        string workerId,
        int maximumMessages,
        TimeSpan leaseDuration,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ValidateWorkerId(workerId);
        if (maximumMessages is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMessages));
        }
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var now = ToText(nowUtc);
        var lockedUntil = ToText(nowUtc.ToUniversalTime().Add(leaseDuration));
        return _writeQueue.ExecuteInTransactionAsync<IReadOnlyList<OutboxMessage>>(
            (connection, transaction, token) =>
            {
                token.ThrowIfCancellationRequested();
                var ids = new List<Guid>();
                using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = """
                        SELECT message_id
                        FROM outbox_messages
                        WHERE
                            ((status IN ('PENDING', 'FAILED') AND available_at_utc <= $nowUtc)
                             OR
                             (status = 'PROCESSING' AND locked_until_utc <= $nowUtc))
                        ORDER BY available_at_utc, created_at_utc, message_id
                        LIMIT $maximumMessages;
                        """;
                    select.Parameters.AddWithValue("$nowUtc", now);
                    select.Parameters.AddWithValue("$maximumMessages", maximumMessages);
                    using var reader = select.ExecuteReader();
                    while (reader.Read())
                    {
                        ids.Add(Guid.Parse(reader.GetString(0)));
                    }
                }

                var claimed = new List<OutboxMessage>(ids.Count);
                foreach (var id in ids)
                {
                    using (var update = connection.CreateCommand())
                    {
                        update.Transaction = transaction;
                        update.CommandText = """
                            UPDATE outbox_messages
                            SET status = 'PROCESSING',
                                attempts = attempts + 1,
                                locked_by = $workerId,
                                locked_until_utc = $lockedUntilUtc,
                                processed_at_utc = NULL
                            WHERE message_id = $messageId
                              AND (((status IN ('PENDING', 'FAILED') AND available_at_utc <= $nowUtc)
                                    OR
                                    (status = 'PROCESSING' AND locked_until_utc <= $nowUtc)));
                            """;
                        update.Parameters.AddWithValue("$workerId", workerId);
                        update.Parameters.AddWithValue("$lockedUntilUtc", lockedUntil);
                        update.Parameters.AddWithValue("$messageId", id.ToString("D"));
                        update.Parameters.AddWithValue("$nowUtc", now);
                        if (update.ExecuteNonQuery() != 1)
                        {
                            continue;
                        }
                    }

                    claimed.Add(ReadById(connection, transaction, id)
                        ?? throw new InvalidOperationException("Claimed Outbox message disappeared inside its transaction."));
                }
                return claimed;
            },
            cancellationToken);
    }

    public ValueTask CompleteAsync(
        Guid messageId,
        string workerId,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ValidateMessageId(messageId);
        ValidateWorkerId(workerId);
        var processedAt = ToText(processedAtUtc);
        return _writeQueue.ExecuteInTransactionAsync(
            (connection, transaction, token) =>
            {
                token.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE outbox_messages
                    SET status = 'PROCESSED',
                        locked_by = NULL,
                        locked_until_utc = NULL,
                        last_error = NULL,
                        processed_at_utc = $processedAtUtc
                    WHERE message_id = $messageId
                      AND status = 'PROCESSING'
                      AND locked_by = $workerId
                      AND locked_until_utc > $processedAtUtc;
                    """;
                command.Parameters.AddWithValue("$processedAtUtc", processedAt);
                command.Parameters.AddWithValue("$messageId", messageId.ToString("D"));
                command.Parameters.AddWithValue("$workerId", workerId);
                if (command.ExecuteNonQuery() != 1)
                {
                    throw new OutboxLeaseException(messageId, workerId);
                }
                return true;
            },
            cancellationToken).AsVoid();
    }

    public ValueTask FailAsync(
        Guid messageId,
        string workerId,
        string error,
        DateTimeOffset failedAtUtc,
        DateTimeOffset nextAttemptAtUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ValidateMessageId(messageId);
        ValidateWorkerId(workerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        if (nextAttemptAtUtc < failedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(nextAttemptAtUtc));
        }
        var boundedError = error.Length <= MaximumErrorCharacters
            ? error
            : error[..MaximumErrorCharacters];
        var failedAt = ToText(failedAtUtc);
        var nextAttempt = ToText(nextAttemptAtUtc);

        return _writeQueue.ExecuteInTransactionAsync(
            (connection, transaction, token) =>
            {
                token.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE outbox_messages
                    SET status = 'FAILED',
                        available_at_utc = $nextAttemptAtUtc,
                        locked_by = NULL,
                        locked_until_utc = NULL,
                        last_error = $lastError,
                        processed_at_utc = NULL
                    WHERE message_id = $messageId
                      AND status = 'PROCESSING'
                      AND locked_by = $workerId
                      AND locked_until_utc > $failedAtUtc;
                    """;
                command.Parameters.AddWithValue("$nextAttemptAtUtc", nextAttempt);
                command.Parameters.AddWithValue("$lastError", boundedError);
                command.Parameters.AddWithValue("$messageId", messageId.ToString("D"));
                command.Parameters.AddWithValue("$workerId", workerId);
                command.Parameters.AddWithValue("$failedAtUtc", failedAt);
                if (command.ExecuteNonQuery() != 1)
                {
                    throw new OutboxLeaseException(messageId, workerId);
                }
                return true;
            },
            cancellationToken).AsVoid();
    }

    public ValueTask<OutboxMessage?> GetAsync(
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ValidateMessageId(messageId);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _connectionFactory.OpenConnection();
        return ValueTask.FromResult(ReadById(connection, transaction: null, messageId));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _writeQueue.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static OutboxMessage? ReadById(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid messageId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT message_id, event_type, schema_version, payload_json,
                   occurred_at_utc, available_at_utc, status, attempts,
                   locked_by, locked_until_utc, last_error, processed_at_utc, created_at_utc
            FROM outbox_messages
            WHERE message_id = $messageId;
            """;
        command.Parameters.AddWithValue("$messageId", messageId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMessage(reader) : null;
    }

    private static OutboxMessage ReadMessage(SqliteDataReader reader)
    {
        return new OutboxMessage(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseTimestamp(reader.GetString(4)),
            ParseTimestamp(reader.GetString(5)),
            ParseStatus(reader.GetString(6)),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : ParseTimestamp(reader.GetString(11)),
            ParseTimestamp(reader.GetString(12)));
    }

    private static void ValidateDraft(OutboxMessageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateMessageId(draft.MessageId);
        ValidateToken(draft.EventType, nameof(draft.EventType), 256);
        ValidateToken(draft.SchemaVersion, nameof(draft.SchemaVersion), 64);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.PayloadJson);
        if (draft.PayloadJson.Length > MaximumPayloadCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(draft), "Outbox payload is too large.");
        }
        try
        {
            using var document = JsonDocument.Parse(draft.PayloadJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Outbox payload must be valid JSON.", nameof(draft), exception);
        }
    }

    private static void ValidateWorkerId(string workerId) =>
        ValidateToken(workerId, nameof(workerId), 128);

    private static void ValidateMessageId(Guid messageId)
    {
        if (messageId == Guid.Empty)
        {
            throw new ArgumentException("Message ID must not be empty.", nameof(messageId));
        }
    }

    private static void ValidateToken(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new ArgumentException($"{parameterName} is invalid.", parameterName);
        }
    }

    private static string ToText(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static OutboxMessageStatus ParseStatus(string value) => value switch
    {
        "PENDING" => OutboxMessageStatus.Pending,
        "PROCESSING" => OutboxMessageStatus.Processing,
        "FAILED" => OutboxMessageStatus.Failed,
        "PROCESSED" => OutboxMessageStatus.Processed,
        _ => throw new InvalidOperationException($"Unknown Outbox status: {value}"),
    };

    private void EnsureActive() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

internal static class OutboxValueTaskExtensions
{
    public static async ValueTask AsVoid<T>(this ValueTask<T> task)
    {
        _ = await task.ConfigureAwait(false);
    }
}

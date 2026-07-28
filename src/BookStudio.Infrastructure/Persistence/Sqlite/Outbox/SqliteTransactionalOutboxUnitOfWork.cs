using System.Globalization;
using System.Text.Json;
using BookStudio.Application.Outbox;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

public sealed class SqliteTransactionalOutboxUnitOfWork : ITransactionalOutboxUnitOfWork, IAsyncDisposable
{
    private const int MaximumOperationIdLength = 256;
    private const int MaximumFingerprintLength = 256;
    private const int MaximumStateKeyLength = 512;
    private const int MaximumStateValueLength = 1_048_576;
    private const int MaximumMessages = 1_000;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteWriteQueue _writeQueue;
    private int _disposed;

    public SqliteTransactionalOutboxUnitOfWork(
        SqliteConnectionFactory connectionFactory,
        int writeQueueCapacity = 64)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _writeQueue = new SqliteWriteQueue(connectionFactory, writeQueueCapacity);
    }

    public ValueTask<TransactionalOutboxResult> ExecuteAsync(
        TransactionalOutboxCommand command,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Validate(command);
        var committedAt = ToText(committedAtUtc);

        return _writeQueue.ExecuteInTransactionAsync(
            (connection, transaction, token) =>
            {
                token.ThrowIfCancellationRequested();

                var replay = ReadOperation(connection, transaction, command.OperationId);
                if (replay is not null)
                {
                    if (!string.Equals(replay.RequestFingerprint, command.RequestFingerprint, StringComparison.Ordinal))
                    {
                        throw Conflict(command.OperationId);
                    }

                    return new TransactionalOutboxResult(
                        command.OperationId,
                        Replayed: true,
                        replay.StateVersion,
                        replay.MessageIds);
                }

                var stateVersion = UpsertState(
                    connection,
                    transaction,
                    command.StateKey,
                    command.StateValue,
                    committedAt);

                var messageIds = new List<Guid>(command.Messages.Count);
                foreach (var message in command.Messages)
                {
                    token.ThrowIfCancellationRequested();
                    InsertMessage(connection, transaction, message, committedAt);
                    messageIds.Add(message.MessageId);
                }

                InsertOperation(
                    connection,
                    transaction,
                    command,
                    stateVersion,
                    messageIds,
                    committedAt);

                return new TransactionalOutboxResult(
                    command.OperationId,
                    Replayed: false,
                    stateVersion,
                    messageIds.AsReadOnly());
            },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _writeQueue.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static OperationReceipt? ReadOperation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT request_fingerprint, state_version, message_ids_json
            FROM transactional_outbox_operations
            WHERE operation_id = $operationId;
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var ids = JsonSerializer.Deserialize<Guid[]>(reader.GetString(2))
            ?? throw new InvalidOperationException("Transactional Outbox receipt contained invalid message IDs.");
        return new OperationReceipt(reader.GetString(0), reader.GetInt64(1), Array.AsReadOnly(ids));
    }

    private static long UpsertState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string stateKey,
        string stateValue,
        string committedAt)
    {
        long nextVersion;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT state_version FROM autopilot_state WHERE state_key = $stateKey;";
            read.Parameters.AddWithValue("$stateKey", stateKey);
            var existing = read.ExecuteScalar();
            nextVersion = existing is null or DBNull ? 1 : checked(Convert.ToInt64(existing, CultureInfo.InvariantCulture) + 1);
        }

        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO autopilot_state(state_key, state_value, state_version, updated_at_utc)
            VALUES($stateKey, $stateValue, $stateVersion, $updatedAtUtc)
            ON CONFLICT(state_key) DO UPDATE SET
                state_value = excluded.state_value,
                state_version = excluded.state_version,
                updated_at_utc = excluded.updated_at_utc;
            """;
        upsert.Parameters.AddWithValue("$stateKey", stateKey);
        upsert.Parameters.AddWithValue("$stateValue", stateValue);
        upsert.Parameters.AddWithValue("$stateVersion", nextVersion);
        upsert.Parameters.AddWithValue("$updatedAtUtc", committedAt);
        upsert.ExecuteNonQuery();
        return nextVersion;
    }

    private static void InsertMessage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OutboxMessageDraft draft,
        string committedAt)
    {
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
        insert.Parameters.AddWithValue("$occurredAtUtc", ToText(draft.OccurredAtUtc));
        insert.Parameters.AddWithValue("$availableAtUtc", ToText(draft.AvailableAtUtc));
        insert.Parameters.AddWithValue("$createdAtUtc", committedAt);
        try
        {
            insert.ExecuteNonQuery();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new TransactionalOutboxException(
                TransactionalOutboxErrorCodes.IdempotencyConflict,
                $"Outbox message '{draft.MessageId:D}' already exists.");
        }
    }

    private static void InsertOperation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransactionalOutboxCommand source,
        long stateVersion,
        IReadOnlyList<Guid> messageIds,
        string committedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO transactional_outbox_operations(
                operation_id, request_fingerprint, state_key, state_version,
                message_ids_json, committed_at_utc)
            VALUES(
                $operationId, $requestFingerprint, $stateKey, $stateVersion,
                $messageIdsJson, $committedAtUtc);
            """;
        command.Parameters.AddWithValue("$operationId", source.OperationId);
        command.Parameters.AddWithValue("$requestFingerprint", source.RequestFingerprint);
        command.Parameters.AddWithValue("$stateKey", source.StateKey);
        command.Parameters.AddWithValue("$stateVersion", stateVersion);
        command.Parameters.AddWithValue("$messageIdsJson", JsonSerializer.Serialize(messageIds));
        command.Parameters.AddWithValue("$committedAtUtc", committedAt);
        command.ExecuteNonQuery();
    }

    private static void Validate(TransactionalOutboxCommand command)
    {
        if (command is null)
        {
            throw Invalid("Command is required.");
        }

        ValidateText(command.OperationId, MaximumOperationIdLength, nameof(command.OperationId));
        ValidateText(command.RequestFingerprint, MaximumFingerprintLength, nameof(command.RequestFingerprint));
        ValidateText(command.StateKey, MaximumStateKeyLength, nameof(command.StateKey));
        if (command.StateValue is null || command.StateValue.Length > MaximumStateValueLength)
        {
            throw Invalid("State value is invalid.");
        }
        if (command.Messages is null || command.Messages.Count is < 1 or > MaximumMessages)
        {
            throw Invalid("At least one bounded Outbox message is required.");
        }

        var ids = new HashSet<Guid>();
        foreach (var message in command.Messages)
        {
            if (message is null || message.MessageId == Guid.Empty || !ids.Add(message.MessageId))
            {
                throw Invalid("Outbox messages contain an invalid or duplicate ID.");
            }
            ValidateText(message.EventType, 256, nameof(message.EventType));
            ValidateText(message.SchemaVersion, 64, nameof(message.SchemaVersion));
            if (string.IsNullOrWhiteSpace(message.PayloadJson) || message.PayloadJson.Length > MaximumStateValueLength)
            {
                throw Invalid("Outbox payload is invalid.");
            }
            try
            {
                using var document = JsonDocument.Parse(message.PayloadJson);
            }
            catch (JsonException)
            {
                throw Invalid("Outbox payload must be valid JSON.");
            }
        }
    }

    private static void ValidateText(string value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw Invalid($"{field} is invalid.");
        }
    }

    private static TransactionalOutboxException Invalid(string message) =>
        new(TransactionalOutboxErrorCodes.Invalid, message);

    private static TransactionalOutboxException Conflict(string operationId) =>
        new(
            TransactionalOutboxErrorCodes.IdempotencyConflict,
            $"Operation '{operationId}' was already committed with a different request fingerprint.");

    private static string ToText(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record OperationReceipt(
        string RequestFingerprint,
        long StateVersion,
        IReadOnlyList<Guid> MessageIds);
}

using BookStudio.Application.Outbox;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class TransactionalOutboxJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "transactional-outbox.db", writeQueueCapacity: 32);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 3, "Transactional Outbox migration was not applied.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 28, 7, 0, 0, TimeSpan.Zero);

        var firstMessageId = Guid.NewGuid();
        var firstCommand = Command(
            "operation-001",
            "fingerprint-001",
            "workflow/book-001",
            "READY",
            Draft(firstMessageId, "workflow.ready", "{\"workflowId\":\"book-001\"}", now));

        await using (var unit = new SqliteTransactionalOutboxUnitOfWork(factory))
        {
            var committed = await unit.ExecuteAsync(firstCommand, now);
            Require(!committed.Replayed && committed.StateVersion == 1, "Initial transaction was not committed.");
            Require(committed.MessageIds.SequenceEqual([firstMessageId]), "Committed receipt lost message IDs.");
            Require(ReadState(factory, firstCommand.StateKey) == ("READY", 1L), "State and Outbox were not committed atomically.");

            var replayed = await unit.ExecuteAsync(firstCommand, now.AddSeconds(1));
            Require(replayed.Replayed && replayed.StateVersion == 1, "Idempotent replay changed state.");
            Require(CountMessages(factory, firstMessageId) == 1, "Idempotent replay duplicated an Outbox message.");

            await RequireCodeAsync(
                () => unit.ExecuteAsync(firstCommand with { RequestFingerprint = "different" }, now.AddSeconds(2)).AsTask(),
                TransactionalOutboxErrorCodes.IdempotencyConflict);

            var preexistingId = Guid.NewGuid();
            await using (var store = new SqliteOutboxStore(factory))
            {
                _ = await store.EnqueueAsync(Draft(preexistingId, "existing", "{}", now), now);
            }

            var rollback = Command(
                "operation-rollback",
                "fingerprint-rollback",
                firstCommand.StateKey,
                "BROKEN",
                Draft(preexistingId, "conflict", "{}", now));
            await RequireCodeAsync(
                () => unit.ExecuteAsync(rollback, now.AddMinutes(1)).AsTask(),
                TransactionalOutboxErrorCodes.IdempotencyConflict);
            Require(ReadState(factory, firstCommand.StateKey) == ("READY", 1L), "Failed enqueue did not roll back state mutation.");
            Require(!OperationExists(factory, rollback.OperationId), "Failed transaction persisted its idempotency receipt.");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await RequireThrowsAsync<OperationCanceledException>(
                () => unit.ExecuteAsync(
                    Command("operation-cancelled", "fingerprint-cancelled", "workflow/cancelled", "X",
                        Draft(Guid.NewGuid(), "cancelled", "{}", now)),
                    now,
                    cancelled.Token).AsTask());
            Require(ReadState(factory, "workflow/cancelled") is null, "Cancelled transaction mutated state.");
        }

        await using (var restartedUnit = new SqliteTransactionalOutboxUnitOfWork(factory))
        {
            var replayAfterRestart = await restartedUnit.ExecuteAsync(firstCommand, now.AddHours(1));
            Require(replayAfterRestart.Replayed, "Committed operation receipt was not durable across restart.");
        }

        await using (var restartedStore = new SqliteOutboxStore(factory))
        {
            var claimed = await restartedStore.ClaimAsync("transactional-worker", 10, TimeSpan.FromMinutes(5), now.AddHours(1));
            Require(claimed.Any(item => item.MessageId == firstMessageId), "Committed event was not recoverable after restart.");
            var message = claimed.Single(item => item.MessageId == firstMessageId);
            await restartedStore.CompleteAsync(message.MessageId, "transactional-worker", now.AddHours(1).AddMinutes(1));
        }
    }

    private static TransactionalOutboxCommand Command(
        string operationId,
        string fingerprint,
        string stateKey,
        string stateValue,
        params OutboxMessageDraft[] messages) =>
        new(operationId, fingerprint, stateKey, stateValue, messages);

    private static OutboxMessageDraft Draft(
        Guid messageId,
        string eventType,
        string payload,
        DateTimeOffset now) =>
        new(messageId, eventType, "1.0.0", payload, now, now);

    private static (string Value, long Version)? ReadState(SqliteConnectionFactory factory, string key)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_value, state_version FROM autopilot_state WHERE state_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetInt64(1)) : null;
    }

    private static bool OperationExists(SqliteConnectionFactory factory, string operationId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM transactional_outbox_operations WHERE operation_id = $id;";
        command.Parameters.AddWithValue("$id", operationId);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    private static long CountMessages(SqliteConnectionFactory factory, Guid messageId)
    {
        using var connection = factory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM outbox_messages WHERE message_id = $id;";
        command.Parameters.AddWithValue("$id", messageId.ToString("D"));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static async Task RequireCodeAsync(Func<Task> action, string code)
    {
        try
        {
            await action();
        }
        catch (TransactionalOutboxException exception) when (exception.Code == code)
        {
            return;
        }
        throw new InvalidOperationException($"Expected Transactional Outbox error '{code}'.");
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

using BookStudio.Application.Outbox;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Integration;

internal static class OutboxJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        Directory.CreateDirectory(workspaceRoot);
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "outbox.db", writeQueueCapacity: 32);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 2, "Outbox migration was not applied.");
        var factory = new SqliteConnectionFactory(options);

        var now = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        var firstId = Guid.NewGuid();
        var firstDraft = Draft(firstId, "book.project-created", "{\"projectId\":\"p-001\"}", now);

        await using (var store = new SqliteOutboxStore(factory))
        {
            await RequireThrowsAsync<ArgumentException>(
                async () => _ = await store.EnqueueAsync(
                    firstDraft with { MessageId = Guid.NewGuid(), PayloadJson = "not-json" },
                    now));

            Require(
                await store.EnqueueAsync(firstDraft, now) == OutboxEnqueueResult.Inserted,
                "First enqueue must insert the message.");
            Require(
                await store.EnqueueAsync(firstDraft, now.AddSeconds(1)) == OutboxEnqueueResult.AlreadyExists,
                "Identical enqueue must be idempotent.");
            await RequireThrowsAsync<OutboxMessageConflictException>(
                async () => _ = await store.EnqueueAsync(
                    firstDraft with { PayloadJson = "{\"projectId\":\"changed\"}" },
                    now.AddSeconds(2)));

            var pending = await store.GetAsync(firstId)
                ?? throw new InvalidOperationException("Enqueued Outbox message was not found.");
            Require(
                pending.Status == OutboxMessageStatus.Pending && pending.Attempts == 0,
                "Enqueued state is invalid.");
            Require(pending.PayloadJson == firstDraft.PayloadJson, "Outbox payload must be preserved exactly.");

            var firstClaim = await store.ClaimAsync("worker-a", 10, TimeSpan.FromMinutes(5), now);
            Require(firstClaim.Count == 1 && firstClaim[0].MessageId == firstId, "Eligible message was not claimed.");
            Require(firstClaim[0].Attempts == 1, "Claim must increment attempts.");
            Require(firstClaim[0].LockedBy == "worker-a", "Claim owner mismatch.");
            Require(
                (await store.ClaimAsync("worker-b", 10, TimeSpan.FromMinutes(5), now.AddMinutes(1))).Count == 0,
                "A live lease must prevent duplicate claim.");

            await RequireThrowsAsync<OutboxLeaseException>(
                async () => await store.CompleteAsync(firstId, "worker-b", now.AddMinutes(2)));
            await store.CompleteAsync(firstId, "worker-a", now.AddMinutes(2));
            var processed = await store.GetAsync(firstId)
                ?? throw new InvalidOperationException("Completed Outbox message was not found.");
            Require(
                processed.Status == OutboxMessageStatus.Processed && processed.ProcessedAtUtc is not null,
                "Completion state is invalid.");

            var retryId = Guid.NewGuid();
            var retryDraft = Draft(retryId, "book.chapter-ready", "{\"chapter\":1}", now);
            _ = await store.EnqueueAsync(retryDraft, now);
            var retryClaim = await store.ClaimAsync("worker-a", 1, TimeSpan.FromMinutes(5), now);
            Require(retryClaim.Single().MessageId == retryId, "Retry message was not claimed.");
            await store.FailAsync(
                retryId,
                "worker-a",
                new string('x', 3_000),
                now.AddMinutes(1),
                now.AddMinutes(10));
            var failed = await store.GetAsync(retryId)
                ?? throw new InvalidOperationException("Failed Outbox message was not found.");
            Require(
                failed.Status == OutboxMessageStatus.Failed && failed.Attempts == 1,
                "Failure state is invalid.");
            Require(failed.LastError?.Length == 2_048, "Failure error must be bounded.");
            Require(
                (await store.ClaimAsync("worker-b", 10, TimeSpan.FromMinutes(5), now.AddMinutes(9))).Count == 0,
                "Failed message must respect retry availability.");

            var crashId = Guid.NewGuid();
            _ = await store.EnqueueAsync(
                Draft(crashId, "book.render-requested", "{\"format\":\"epub\"}", now),
                now);
            var crashClaim = await store.ClaimAsync("worker-crashed", 1, TimeSpan.FromMinutes(1), now);
            Require(crashClaim.Single().MessageId == crashId, "Crash-recovery message was not claimed.");
        }

        await using (var restarted = new SqliteOutboxStore(factory))
        {
            var reclaimed = await restarted.ClaimAsync(
                "worker-recovery",
                10,
                TimeSpan.FromMinutes(5),
                now.AddMinutes(2));
            Require(reclaimed.Count == 1 && reclaimed[0].Attempts == 2, "Expired lease was not reclaimed after restart.");
            Require(reclaimed[0].LockedBy == "worker-recovery", "Recovered lease owner mismatch.");
            await restarted.CompleteAsync(
                reclaimed[0].MessageId,
                "worker-recovery",
                now.AddMinutes(3));

            var retried = await restarted.ClaimAsync(
                "worker-retry",
                10,
                TimeSpan.FromMinutes(5),
                now.AddMinutes(10));
            Require(retried.Count == 1 && retried[0].MessageId != reclaimed[0].MessageId, "Failed message was not retried.");
            Require(retried[0].Attempts == 2, "Retry claim must increment attempts.");
            await restarted.CompleteAsync(retried[0].MessageId, "worker-retry", now.AddMinutes(11));

            var delayedId = Guid.NewGuid();
            _ = await restarted.EnqueueAsync(
                Draft(delayedId, "book.delayed", "{}", now.AddDays(1)),
                now);
            Require(
                (await restarted.ClaimAsync("worker-a", 10, TimeSpan.FromMinutes(5), now.AddHours(1))).Count == 0,
                "Future message must not be claimed early.");

            await RequireThrowsAsync<ArgumentOutOfRangeException>(
                async () => _ = await restarted.ClaimAsync("worker-a", 1, TimeSpan.Zero, now));
        }

        var raceId = Guid.NewGuid();
        await using (var enqueueStore = new SqliteOutboxStore(factory))
        {
            _ = await enqueueStore.EnqueueAsync(
                Draft(raceId, "book.concurrent-dispatch", "{\"race\":true}", now.AddHours(2)),
                now);
        }

        await using (var raceStoreA = new SqliteOutboxStore(factory))
        await using (var raceStoreB = new SqliteOutboxStore(factory))
        {
            var claims = await Task.WhenAll(
                raceStoreA.ClaimAsync("worker-race-a", 1, TimeSpan.FromMinutes(5), now.AddHours(2)).AsTask(),
                raceStoreB.ClaimAsync("worker-race-b", 1, TimeSpan.FromMinutes(5), now.AddHours(2)).AsTask());
            Require(claims.Sum(batch => batch.Count) == 1, "Two store instances must not claim the same message.");
            var winner = claims.Single(batch => batch.Count == 1).Single();
            await (winner.LockedBy == "worker-race-a" ? raceStoreA : raceStoreB)
                .CompleteAsync(winner.MessageId, winner.LockedBy!, now.AddHours(2).AddMinutes(1));
        }

        var disposedStore = new SqliteOutboxStore(factory);
        await disposedStore.DisposeAsync();
        await RequireThrowsAsync<ObjectDisposedException>(
            async () => _ = await disposedStore.GetAsync(firstId));
    }

    private static OutboxMessageDraft Draft(
        Guid id,
        string eventType,
        string payload,
        DateTimeOffset availableAt) =>
        new(
            id,
            eventType,
            "1.0.0",
            payload,
            availableAt,
            availableAt);

    private static async Task RequireThrowsAsync<TException>(Func<Task> operation)
        where TException : Exception
    {
        try
        {
            await operation();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected exception {typeof(TException).Name} was not thrown.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

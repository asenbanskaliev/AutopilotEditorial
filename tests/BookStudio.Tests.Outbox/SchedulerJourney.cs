using BookStudio.Application.Autopilot;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Autopilot;

namespace BookStudio.Tests.Outbox;

internal static class SchedulerJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "scheduler.db", writeQueueCapacity: 32);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 4, "Scheduler migration was not applied.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 28, 8, 0, 0, TimeSpan.Zero);
        var low = Draft(Guid.NewGuid(), "low", 1, now);
        var high = Draft(Guid.NewGuid(), "high", 100, now);
        var later = Draft(Guid.NewGuid(), "later", 100, now.AddHours(1));

        await using (var store = new SqliteJobSchedulerStore(factory))
        {
            Require(await store.CreateAsync(low, now) == JobCreateResult.Inserted, "Low-priority job was not inserted.");
            Require(await store.CreateAsync(high, now.AddSeconds(1)) == JobCreateResult.Inserted, "High-priority job was not inserted.");
            Require(await store.CreateAsync(later, now.AddSeconds(2)) == JobCreateResult.Inserted, "Future job was not inserted.");
            Require(await store.CreateAsync(high, now.AddSeconds(3)) == JobCreateResult.AlreadyExists, "Identical create was not idempotent.");
            await RequireThrowsAsync<JobConflictException>(() => store.CreateAsync(high with { Priority = 99 }, now).AsTask());

            var first = await store.ClaimAsync("worker-a", 1, TimeSpan.FromMinutes(5), now);
            Require(first.Single().JobId == high.JobId, "Priority order was not deterministic.");
            Require(first[0].Attempts == 1 && first[0].LockedBy == "worker-a", "Claim lease was invalid.");
            Require((await store.ClaimAsync("worker-b", 10, TimeSpan.FromMinutes(5), now.AddMinutes(1))).Single().JobId == low.JobId,
                "Live lease or priority ordering failed.");

            await store.RenewAsync(high.JobId, "worker-a", TimeSpan.FromMinutes(5), now.AddMinutes(2));
            await RequireThrowsAsync<JobLeaseException>(() => store.CompleteAsync(high.JobId, "worker-b", now.AddMinutes(3)).AsTask());
            await store.CompleteAsync(high.JobId, "worker-a", now.AddMinutes(3));

            await store.FailAsync(low.JobId, "worker-b", new string('x', 3000), now.AddMinutes(2), now.AddMinutes(10));
            var failed = await store.GetAsync(low.JobId) ?? throw new InvalidOperationException("Failed job missing.");
            Require(failed.Status == JobStatus.Failed && failed.LastError?.Length == 2048, "Failure/retry state was invalid.");

            var crash = Draft(Guid.NewGuid(), "crash", 50, now);
            _ = await store.CreateAsync(crash, now);
            var crashClaim = await store.ClaimAsync("worker-crashed", 1, TimeSpan.FromMinutes(1), now);
            Require(crashClaim.Single().JobId == crash.JobId, "Crash job was not claimed.");
        }

        await using (var restarted = new SqliteJobSchedulerStore(factory))
        {
            var reclaimed = await restarted.ClaimAsync("worker-recovery", 10, TimeSpan.FromMinutes(5), now.AddMinutes(2));
            Require(reclaimed.Any(item => item.JobId != later.JobId && item.Attempts >= 2), "Expired job was not reclaimed after restart.");
            foreach (var item in reclaimed)
            {
                await restarted.CompleteAsync(item.JobId, "worker-recovery", now.AddMinutes(3));
            }

            Require((await restarted.ClaimAsync("worker-before-retry", 10, TimeSpan.FromMinutes(5), now.AddMinutes(9))).Count == 0,
                "Retry or future job was claimed before availability.");

            var retry = await restarted.ClaimAsync("worker-retry", 10, TimeSpan.FromMinutes(5), now.AddMinutes(10));
            Require(retry.Any(item => item.JobId == low.JobId && item.Attempts == 2), "Failed job was not retried.");
            foreach (var item in retry)
            {
                await restarted.CompleteAsync(item.JobId, "worker-retry", now.AddMinutes(11));
            }

            Require((await restarted.ClaimAsync("worker-before-future", 10, TimeSpan.FromMinutes(5), now.AddMinutes(30))).Count == 0,
                "Future job was claimed early.");

            var future = await restarted.ClaimAsync("worker-future", 10, TimeSpan.FromMinutes(5), now.AddHours(1));
            Require(future.Single().JobId == later.JobId, "Scheduled availability was not respected.");
            await restarted.CompleteAsync(later.JobId, "worker-future", now.AddHours(1).AddMinutes(1));
        }
    }

    private static JobDraft Draft(Guid id, string type, int priority, DateTimeOffset availableAt) =>
        new(id, type, "1.0.0", "{}", priority, availableAt);

    private static async Task RequireThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

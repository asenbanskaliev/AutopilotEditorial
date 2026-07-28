using BookStudio.Application.Autopilot;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Autopilot;

namespace BookStudio.Tests.Outbox;

internal static class ConcurrencyLimitJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "concurrency.db", 64);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 8, "Concurrency migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 28, 10, 30, 0, TimeSpan.Zero);

        var global = new ConcurrencyLimitDefinition(ConcurrencyScopeType.Global, "default", 2, 1, "operator");
        var provider = new ConcurrencyLimitDefinition(ConcurrencyScopeType.Provider, "openai", 1, 1, "operator");
        await using (var store = new SqliteConcurrencyLimitStore(factory))
        {
            _ = await store.UpsertLimitAsync(global, now);
            _ = await store.UpsertLimitAsync(provider, now);
            await RequireThrowsAsync<ConcurrencyLimitConflictException>(() => store.UpsertLimitAsync(global with { Capacity = 3 }, now).AsTask());
            _ = await store.UpsertLimitAsync(global with { Capacity = 3, Version = 2 }, now.AddSeconds(1));

            var scopes = new[]
            {
                new ConcurrencyScopeRequest(ConcurrencyScopeType.Global, "default", 1),
                new ConcurrencyScopeRequest(ConcurrencyScopeType.Provider, "openai", 1),
            };
            var acquire = new ConcurrencyAcquireCommand(Guid.NewGuid(), "worker-1", 10, TimeSpan.FromMinutes(5), scopes, "acquire-1");
            var first = await store.AcquireAsync(acquire, now.AddMinutes(1));
            Require(first.Outcome == ConcurrencyAcquireOutcome.Granted && first.Grant is not null && first.Grant.Generation == 1, "Atomic multi-scope acquire failed.");
            var replay = await store.AcquireAsync(acquire, now.AddMinutes(1));
            Require(replay.Replayed && replay.Grant?.GrantId == first.Grant.GrantId, "Acquire replay was not idempotent.");
            await RequireThrowsAsync<ConcurrencyLimitConflictException>(() => store.AcquireAsync(acquire with { OwnerId = "worker-x" }, now.AddMinutes(1)).AsTask());

            var blockedCommand = new ConcurrencyAcquireCommand(Guid.NewGuid(), "worker-2", 5, TimeSpan.FromMinutes(5), scopes, "acquire-2");
            var blocked = await store.AcquireAsync(blockedCommand, now.AddMinutes(1));
            Require(blocked.Outcome == ConcurrencyAcquireOutcome.CapacityUnavailable && blocked.Grant is null, "Provider capacity was overcommitted.");

            var renewed = await store.RenewAsync(new ConcurrencyRenewCommand(Guid.NewGuid(), first.Grant.GrantId, first.Grant.Generation, "worker-1", TimeSpan.FromMinutes(10), "renew-1"), now.AddMinutes(2));
            Require(renewed.Generation == 2 && renewed.LeaseUntilUtc == now.AddMinutes(12), "Lease renewal failed.");
            await RequireThrowsAsync<ConcurrencyLeaseException>(() => store.RenewAsync(new ConcurrencyRenewCommand(Guid.NewGuid(), renewed.GrantId, 1, "worker-1", TimeSpan.FromMinutes(1), "stale"), now.AddMinutes(3)).AsTask());

            var releaseCommand = new ConcurrencyReleaseCommand(Guid.NewGuid(), renewed.GrantId, renewed.Generation, "worker-1", "release-1");
            var released = await store.ReleaseAsync(releaseCommand, now.AddMinutes(3));
            Require(released.Grant.Status == ConcurrencyGrantStatus.Released && !released.Replayed, "Release failed.");
            Require((await store.ReleaseAsync(releaseCommand, now.AddMinutes(4))).Replayed, "Release replay was not idempotent.");

            var second = await store.AcquireAsync(new ConcurrencyAcquireCommand(Guid.NewGuid(), "worker-2", 5, TimeSpan.FromMinutes(1), scopes, "acquire-3"), now.AddMinutes(4));
            Require(second.Outcome == ConcurrencyAcquireOutcome.Granted && second.Grant is not null, "Capacity was not restored after release.");
            Require(await store.ReclaimExpiredAsync(now.AddMinutes(6)) == 1, "Expired lease was not reclaimed.");
            Require((await store.GetGrantAsync(second.Grant.GrantId))?.Status == ConcurrencyGrantStatus.Expired, "Expired state was not durable.");
            await RequireThrowsAsync<ConcurrencyLeaseException>(() => store.ReleaseAsync(new ConcurrencyReleaseCommand(Guid.NewGuid(), second.Grant.GrantId, second.Grant.Generation, "worker-2", "late-release"), now.AddMinutes(6)).AsTask());
        }

        await using var restarted = new SqliteConcurrencyLimitStore(factory);
        var recovered = await restarted.AcquireAsync(new ConcurrencyAcquireCommand(
            Guid.NewGuid(), "worker-3", 1, TimeSpan.FromMinutes(5),
            new[] { new ConcurrencyScopeRequest(ConcurrencyScopeType.Provider, "openai", 1) }, "restart-acquire"), now.AddMinutes(7));
        Require(recovered.Outcome == ConcurrencyAcquireOutcome.Granted, "Capacity was not recovered across restart.");
    }

    private static async Task RequireThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

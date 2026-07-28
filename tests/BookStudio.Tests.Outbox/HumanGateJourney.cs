using BookStudio.Application.Autopilot;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Autopilot;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class HumanGateJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "human-gates.db", 32);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 5, "Human-gate migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero);
        var draft = new HumanGateDraft(Guid.NewGuid(), "book-authoring", "1.0.0", "approve-spec", Guid.NewGuid(), "Approve specification?", "1.0.0", now.AddHours(1));

        await using (var store = new SqliteHumanGateStore(factory))
        {
            Require(await store.CreateAsync(draft, now) == HumanGateCreateResult.Inserted, "Gate was not created.");
            Require(await store.CreateAsync(draft, now) == HumanGateCreateResult.AlreadyExists, "Creation was not idempotent.");
            await RequireThrowsAsync<HumanGateConflictException>(() => store.CreateAsync(draft with { Prompt = "Different" }, now).AsTask());
            var claimed = await store.ClaimAsync(draft.RequestId, "editor-1", TimeSpan.FromMinutes(10), now.AddMinutes(1));
            Require(claimed.Status == HumanGateStatus.Claimed && claimed.ClaimedBy == "editor-1", "Claim failed.");
            await RequireThrowsAsync<HumanGateLeaseException>(() => store.DecideAsync(draft.RequestId, "editor-2", HumanGateDecision.Approve, "ok", now.AddMinutes(2)).AsTask());
            var decision = await store.DecideAsync(draft.RequestId, "editor-1", HumanGateDecision.Approve, "ok", now.AddMinutes(2));
            Require(!decision.Replayed && decision.Request.Status == HumanGateStatus.Approved && decision.Request.ResumeMessageId is not null, "Decision failed.");
            var replay = await store.DecideAsync(draft.RequestId, "editor-1", HumanGateDecision.Approve, "ok", now.AddMinutes(3));
            Require(replay.Replayed && replay.Request.ResumeMessageId == decision.Request.ResumeMessageId, "Decision replay duplicated resume.");
            await RequireThrowsAsync<HumanGateConflictException>(() => store.DecideAsync(draft.RequestId, "editor-1", HumanGateDecision.Reject, "no", now.AddMinutes(4)).AsTask());

            var expiring = draft with { RequestId = Guid.NewGuid(), JobId = Guid.NewGuid(), ExpiresAtUtc = now.AddMinutes(5) };
            _ = await store.CreateAsync(expiring, now);
            Require(await store.ExpireDueAsync(now.AddMinutes(6)) == 1, "Expiry sweep failed.");
            Require((await store.GetAsync(expiring.RequestId))?.Status == HumanGateStatus.Expired, "Gate was not expired.");

            var cancelled = draft with { RequestId = Guid.NewGuid(), JobId = Guid.NewGuid() };
            _ = await store.CreateAsync(cancelled, now);
            Require((await store.CancelAsync(cancelled.RequestId, "operator", now.AddMinutes(1))).Status == HumanGateStatus.Cancelled, "Cancellation failed.");
        }

        await using var restarted = new SqliteHumanGateStore(factory);
        var durable = await restarted.GetAsync(draft.RequestId);
        Require(durable?.Status == HumanGateStatus.Approved, "Decision was not durable across restart.");
        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("gate-worker", 20, TimeSpan.FromMinutes(5), now.AddHours(2));
        Require(messages.Count(item => item.MessageId == durable.ResumeMessageId) == 1, "Resume command was not emitted exactly once.");
    }

    private static async Task RequireThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

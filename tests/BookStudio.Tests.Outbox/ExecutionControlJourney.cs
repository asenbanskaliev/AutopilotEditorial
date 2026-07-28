using BookStudio.Application.Autopilot;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Autopilot;
using BookStudio.Infrastructure.Persistence.Sqlite.Outbox;

namespace BookStudio.Tests.Outbox;

internal static class ExecutionControlJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "execution-control.db", 32);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 6, "Execution-control migration missing.");
        var factory = new SqliteConnectionFactory(options);
        var now = new DateTimeOffset(2026, 7, 28, 10, 0, 0, TimeSpan.Zero);
        var executionId = Guid.NewGuid();

        await using (var store = new SqliteExecutionControlStore(factory))
        {
            var pause = Command(executionId, ExecutionControlAction.Pause, "pause-1");
            var paused = await store.ApplyAsync(pause, now);
            Require(!paused.Replayed && paused.Execution.Status == ExecutionControlStatus.Paused && paused.Execution.Version == 1, "Pause failed.");
            var pauseReplay = await store.ApplyAsync(pause, now.AddSeconds(1));
            Require(pauseReplay.Replayed && pauseReplay.ControlMessageId == paused.ControlMessageId, "Pause replay was not idempotent.");
            await RequireThrowsAsync<ExecutionControlConflictException>(() => store.ApplyAsync(pause with { Reason = "different" }, now.AddSeconds(2)).AsTask());
            await RequireThrowsAsync<ExecutionControlTransitionException>(() => store.ApplyAsync(Command(executionId, ExecutionControlAction.Pause, "pause-2"), now.AddSeconds(3)).AsTask());

            var resumed = await store.ApplyAsync(Command(executionId, ExecutionControlAction.Resume, "resume-1"), now.AddMinutes(1));
            Require(resumed.Execution.Status == ExecutionControlStatus.Runnable && resumed.Execution.Version == 2, "Resume failed.");
            var cancelled = await store.ApplyAsync(Command(executionId, ExecutionControlAction.Cancel, "cancel-1"), now.AddMinutes(2));
            Require(cancelled.Execution.Status == ExecutionControlStatus.Cancelled && cancelled.Execution.Version == 3, "Cancel failed.");
            await RequireThrowsAsync<ExecutionControlTransitionException>(() => store.ApplyAsync(Command(executionId, ExecutionControlAction.Resume, "resume-after-cancel"), now.AddMinutes(3)).AsTask());
        }

        await using var restarted = new SqliteExecutionControlStore(factory);
        var durable = await restarted.GetAsync(executionId) ?? throw new InvalidOperationException("Execution state missing after restart.");
        Require(durable.Status == ExecutionControlStatus.Cancelled && durable.Version == 3, "Execution control was not durable.");

        await using var outbox = new SqliteOutboxStore(factory);
        var messages = await outbox.ClaimAsync("control-worker", 20, TimeSpan.FromMinutes(5), now.AddHours(1));
        Require(messages.Count == 3, "Control events were not emitted exactly once.");
        Require(messages.Select(item => item.MessageId).Distinct().Count() == 3, "Control event IDs are not unique.");
    }

    private static ExecutionControlCommand Command(Guid executionId, ExecutionControlAction action, string seed) =>
        new(Guid.NewGuid(), executionId, action, "operator", seed, $"sha256:{seed}");

    private static async Task RequireThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

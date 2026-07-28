using BookStudio.Application.Autopilot;
using BookStudio.Infrastructure.Persistence.Sqlite;
using BookStudio.Infrastructure.Persistence.Sqlite.Autopilot;

namespace BookStudio.Tests.Outbox;

internal static class WorkerExecutionJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var options = SqliteWorkspaceOptions.Create(workspaceRoot, "worker.db", writeQueueCapacity: 32);
        await using var database = new SqliteWorkspaceDatabase(options);
        var health = await database.InitializeAsync();
        Require(health.LatestMigrationVersion >= 4, "Scheduler migration was not applied for worker execution.");
        var factory = new SqliteConnectionFactory(options);
        var now = DateTimeOffset.UtcNow;

        var heartbeatHandler = new HeartbeatHandler();
        var failureHandler = new FailureHandler();
        var timeoutHandler = new TimeoutHandler();

        await using (var store = new SqliteJobSchedulerStore(factory))
        {
            await RequireThrowsAsync<ArgumentException>(() => Task.FromResult(new JobWorker(
                store,
                [heartbeatHandler, new DuplicateHeartbeatHandler()],
                WorkerExecutionOptions.Create("duplicate-worker"))));

            var successId = Guid.NewGuid();
            _ = await store.CreateAsync(Draft(successId, "heartbeat", 100, now), now);
            var successWorker = new JobWorker(
                store,
                [heartbeatHandler],
                new WorkerExecutionOptions("worker-success", 1, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2), TimeSpan.FromHours(1)));
            var success = await successWorker.RunOnceAsync(now);
            Require(success == new WorkerIterationReport(1, 1, 0, 0, 0), "Successful worker report was invalid.");
            Require(heartbeatHandler.Heartbeats == 1, "Handler heartbeat was not forwarded to lease renewal.");
            Require((await store.GetAsync(successId))?.Status == JobStatus.Completed, "Successful job was not completed.");

            var failureId = Guid.NewGuid();
            _ = await store.CreateAsync(Draft(failureId, "failure", 90, now), now);
            var failureWorker = new JobWorker(
                store,
                [failureHandler],
                new WorkerExecutionOptions("worker-failure", 1, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2), TimeSpan.FromHours(1)));
            var failure = await failureWorker.RunOnceAsync(now);
            Require(failure == new WorkerIterationReport(1, 0, 1, 0, 0), "Failure worker report was invalid.");
            var failedJob = await store.GetAsync(failureId) ?? throw new InvalidOperationException("Failed worker job missing.");
            Require(failedJob.Status == JobStatus.Failed && failedJob.LastError?.Contains("controlled-handler-failure", StringComparison.Ordinal) == true,
                "Handler failure was not scheduled for retry.");

            var timeoutId = Guid.NewGuid();
            _ = await store.CreateAsync(Draft(timeoutId, "timeout", 80, now), now);
            var timeoutWorker = new JobWorker(
                store,
                [timeoutHandler],
                new WorkerExecutionOptions("worker-timeout", 1, TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(50), TimeSpan.FromHours(1)));
            var timedOut = await timeoutWorker.RunOnceAsync(now);
            Require(timedOut == new WorkerIterationReport(1, 0, 0, 1, 0), "Timeout worker report was invalid.");
            var timeoutJob = await store.GetAsync(timeoutId) ?? throw new InvalidOperationException("Timed-out worker job missing.");
            Require(timeoutJob.Status == JobStatus.Failed && timeoutJob.LastError == "JOB_EXECUTION_TIMEOUT",
                "Timeout did not schedule a bounded retry.");

            var unknownId = Guid.NewGuid();
            _ = await store.CreateAsync(Draft(unknownId, "unknown", 70, now), now);
            var unknownWorker = new JobWorker(
                store,
                Array.Empty<IJobHandler>(),
                new WorkerExecutionOptions("worker-unknown", 1, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2), TimeSpan.FromHours(1)));
            var unknown = await unknownWorker.RunOnceAsync(now);
            Require(unknown == new WorkerIterationReport(1, 0, 1, 0, 0), "Unknown handler report was invalid.");
        }

        var leaseLossId = Guid.NewGuid();
        var gateHandler = new GateHandler();
        await using (var workerStore = new SqliteJobSchedulerStore(factory))
        await using (var recoveryStore = new SqliteJobSchedulerStore(factory))
        {
            _ = await workerStore.CreateAsync(Draft(leaseLossId, "gate", 1000, now), now);
            var leaseLossWorker = new JobWorker(
                workerStore,
                [gateHandler],
                new WorkerExecutionOptions("worker-stale", 1, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5), TimeSpan.FromHours(1)));

            var execution = leaseLossWorker.RunOnceAsync(now).AsTask();
            await gateHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var reclaimed = await recoveryStore.ClaimAsync("worker-recovery", 1, TimeSpan.FromMinutes(5), now.AddMilliseconds(200));
            Require(reclaimed.Single().JobId == leaseLossId, "Expired worker lease was not reclaimed.");
            await recoveryStore.CompleteAsync(leaseLossId, "worker-recovery", now.AddMilliseconds(300));
            gateHandler.Release.TrySetResult(true);

            var leaseLoss = await execution;
            Require(leaseLoss == new WorkerIterationReport(1, 0, 0, 0, 1), "Stale worker did not report lease loss.");
            Require((await recoveryStore.GetAsync(leaseLossId))?.Status == JobStatus.Completed,
                "Stale worker overwrote the recovery owner's completion.");
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

    private sealed class HeartbeatHandler : IJobHandler
    {
        public string JobType => "heartbeat";
        public string SchemaVersion => "1.0.0";
        public int Heartbeats { get; private set; }

        public async ValueTask HandleAsync(JobExecutionContext context, CancellationToken cancellationToken)
        {
            await context.HeartbeatAsync(cancellationToken);
            Heartbeats++;
        }
    }

    private sealed class DuplicateHeartbeatHandler : IJobHandler
    {
        public string JobType => "heartbeat";
        public string SchemaVersion => "1.0.0";
        public ValueTask HandleAsync(JobExecutionContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class FailureHandler : IJobHandler
    {
        public string JobType => "failure";
        public string SchemaVersion => "1.0.0";
        public ValueTask HandleAsync(JobExecutionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromException(new InvalidOperationException("controlled-handler-failure"));
    }

    private sealed class TimeoutHandler : IJobHandler
    {
        public string JobType => "timeout";
        public string SchemaVersion => "1.0.0";
        public async ValueTask HandleAsync(JobExecutionContext context, CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class GateHandler : IJobHandler
    {
        public string JobType => "gate";
        public string SchemaVersion => "1.0.0";
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask HandleAsync(JobExecutionContext context, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
        }
    }
}

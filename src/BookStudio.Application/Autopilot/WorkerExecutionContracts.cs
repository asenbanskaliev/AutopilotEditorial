namespace BookStudio.Application.Autopilot;

public interface IJobHandler
{
    string JobType { get; }
    string SchemaVersion { get; }
    ValueTask HandleAsync(JobExecutionContext context, CancellationToken cancellationToken);
}

public sealed record JobExecutionContext(
    ScheduledJob Job,
    Func<CancellationToken, ValueTask> HeartbeatAsync);

public sealed record WorkerExecutionOptions(
    string WorkerId,
    int MaximumJobs,
    TimeSpan LeaseDuration,
    TimeSpan ExecutionTimeout,
    TimeSpan RetryDelay)
{
    public static WorkerExecutionOptions Create(string workerId) =>
        new(workerId, 1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
}

public sealed record WorkerIterationReport(
    int Claimed,
    int Completed,
    int Failed,
    int TimedOut,
    int LeaseLost);

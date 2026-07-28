namespace BookStudio.Application.Autopilot;

public interface IJobSchedulerStore
{
    ValueTask<JobCreateResult> CreateAsync(JobDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ScheduledJob>> ClaimAsync(string workerId, int maximumJobs, TimeSpan leaseDuration, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    ValueTask RenewAsync(Guid jobId, string workerId, TimeSpan leaseDuration, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    ValueTask CompleteAsync(Guid jobId, string workerId, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default);
    ValueTask FailAsync(Guid jobId, string workerId, string error, DateTimeOffset failedAtUtc, DateTimeOffset retryAtUtc, CancellationToken cancellationToken = default);
    ValueTask<ScheduledJob?> GetAsync(Guid jobId, CancellationToken cancellationToken = default);
}

public sealed record JobDraft(
    Guid JobId,
    string JobType,
    string SchemaVersion,
    string PayloadJson,
    int Priority,
    DateTimeOffset AvailableAtUtc);

public sealed record ScheduledJob(
    Guid JobId,
    string JobType,
    string SchemaVersion,
    string PayloadJson,
    int Priority,
    DateTimeOffset AvailableAtUtc,
    JobStatus Status,
    int Attempts,
    string? LockedBy,
    DateTimeOffset? LockedUntilUtc,
    string? LastError,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset CreatedAtUtc);

public enum JobStatus
{
    Queued,
    Running,
    Failed,
    Completed,
}

public enum JobCreateResult
{
    Inserted,
    AlreadyExists,
}

public sealed class JobConflictException : Exception
{
    public JobConflictException(Guid jobId) : base($"Job '{jobId:D}' already exists with different immutable content.") => JobId = jobId;
    public Guid JobId { get; }
}

public sealed class JobLeaseException : Exception
{
    public JobLeaseException(Guid jobId, string workerId) : base($"Worker '{workerId}' does not own a live lease for job '{jobId:D}'.")
    {
        JobId = jobId;
        WorkerId = workerId;
    }
    public Guid JobId { get; }
    public string WorkerId { get; }
}

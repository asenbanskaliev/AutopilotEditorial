namespace BookStudio.Application.Autopilot;

public sealed class JobWorker
{
    private const int MaximumErrorCharacters = 2_048;
    private readonly IJobSchedulerStore _store;
    private readonly IReadOnlyDictionary<string, IJobHandler> _handlers;
    private readonly WorkerExecutionOptions _options;

    public JobWorker(
        IJobSchedulerStore store,
        IEnumerable<IJobHandler> handlers,
        WorkerExecutionOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = Validate(options);
        ArgumentNullException.ThrowIfNull(handlers);

        var registry = new Dictionary<string, IJobHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ValidateToken(handler.JobType, nameof(handler.JobType), 256);
            ValidateToken(handler.SchemaVersion, nameof(handler.SchemaVersion), 64);
            var key = Key(handler.JobType, handler.SchemaVersion);
            if (!registry.TryAdd(key, handler))
            {
                throw new ArgumentException($"Duplicate job handler registration for '{handler.JobType}' version '{handler.SchemaVersion}'.", nameof(handlers));
            }
        }
        _handlers = registry;
    }

    public async ValueTask<WorkerIterationReport> RunOnceAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var claimed = await _store.ClaimAsync(
            _options.WorkerId,
            _options.MaximumJobs,
            _options.LeaseDuration,
            nowUtc,
            cancellationToken).ConfigureAwait(false);

        var completed = 0;
        var failed = 0;
        var timedOut = 0;
        var leaseLost = 0;

        foreach (var job in claimed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Key(job.JobType, job.SchemaVersion);
            if (!_handlers.TryGetValue(key, out var handler))
            {
                try
                {
                    await _store.FailAsync(
                        job.JobId,
                        _options.WorkerId,
                        Bounded($"No handler registered for '{job.JobType}' version '{job.SchemaVersion}'."),
                        nowUtc,
                        nowUtc.Add(_options.RetryDelay),
                        cancellationToken).ConfigureAwait(false);
                    failed++;
                }
                catch (JobLeaseException)
                {
                    leaseLost++;
                }
                continue;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ExecutionTimeout);
            var heartbeat = new Func<CancellationToken, ValueTask>(token =>
                _store.RenewAsync(job.JobId, _options.WorkerId, _options.LeaseDuration, DateTimeOffset.UtcNow, token));
            var context = new JobExecutionContext(job, heartbeat);

            try
            {
                await handler.HandleAsync(context, timeout.Token).ConfigureAwait(false);
                await _store.CompleteAsync(job.JobId, _options.WorkerId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                completed++;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                try
                {
                    var failedAt = DateTimeOffset.UtcNow;
                    await _store.FailAsync(
                        job.JobId,
                        _options.WorkerId,
                        "JOB_EXECUTION_TIMEOUT",
                        failedAt,
                        failedAt.Add(_options.RetryDelay),
                        CancellationToken.None).ConfigureAwait(false);
                    timedOut++;
                }
                catch (JobLeaseException)
                {
                    leaseLost++;
                }
            }
            catch (JobLeaseException)
            {
                leaseLost++;
            }
            catch (Exception exception)
            {
                try
                {
                    var failedAt = DateTimeOffset.UtcNow;
                    await _store.FailAsync(
                        job.JobId,
                        _options.WorkerId,
                        Bounded($"{exception.GetType().Name}: {exception.Message}"),
                        failedAt,
                        failedAt.Add(_options.RetryDelay),
                        CancellationToken.None).ConfigureAwait(false);
                    failed++;
                }
                catch (JobLeaseException)
                {
                    leaseLost++;
                }
            }
        }

        return new WorkerIterationReport(claimed.Count, completed, failed, timedOut, leaseLost);
    }

    private static WorkerExecutionOptions Validate(WorkerExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateToken(options.WorkerId, nameof(options.WorkerId), 128);
        if (options.MaximumJobs is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaximumJobs));
        }
        if (options.LeaseDuration <= TimeSpan.Zero || options.LeaseDuration > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(options.LeaseDuration));
        }
        if (options.ExecutionTimeout <= TimeSpan.Zero || options.ExecutionTimeout > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options.ExecutionTimeout));
        }
        if (options.RetryDelay < TimeSpan.Zero || options.RetryDelay > TimeSpan.FromDays(30))
        {
            throw new ArgumentOutOfRangeException(nameof(options.RetryDelay));
        }
        return options;
    }

    private static string Key(string jobType, string schemaVersion) => $"{jobType}\u001f{schemaVersion}";

    private static string Bounded(string value) =>
        value.Length <= MaximumErrorCharacters ? value : value[..MaximumErrorCharacters];

    private static void ValidateToken(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new ArgumentException($"{parameterName} is invalid.", parameterName);
        }
    }
}

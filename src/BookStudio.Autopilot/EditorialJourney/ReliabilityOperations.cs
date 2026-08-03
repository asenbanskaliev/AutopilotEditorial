using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BookStudio.Autopilot.EditorialJourney;

public sealed record ResilientOperationOptions(int MaximumAttempts = 3, int CircuitFailureThreshold = 3, TimeSpan? Timeout = null, TimeSpan? BreakDuration = null)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromSeconds(30);
    public TimeSpan EffectiveBreakDuration => BreakDuration ?? TimeSpan.FromMinutes(1);
}
public sealed record OperationTelemetry(string Operation, int Attempt, string Outcome, long DurationMilliseconds, string? Error);
public interface IOperationDelay { ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken); }
public interface IOperationTelemetrySink { ValueTask WriteAsync(OperationTelemetry telemetry, CancellationToken cancellationToken); }
public sealed class CircuitOpenException(string operation) : InvalidOperationException($"Circuit is open for {operation}.");
public sealed class TransientOperationException(string message) : Exception(message);

public sealed class SecretRedactor(IEnumerable<string> secrets)
{
    private readonly string[] _secrets = secrets.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
    public string Redact(string value)
    {
        foreach (var secret in _secrets) value = value.Replace(secret, "***", StringComparison.Ordinal);
        value = Regex.Replace(value, "(?i)(api[_-]?key|token|authorization)\\s*[:=]\\s*[^\\s,;]+", "$1=***");
        return value.Length <= 1000 ? value : value[^1000..];
    }
}

public sealed class ResilientOperationExecutor
{
    private readonly ResilientOperationOptions _options;
    private readonly IOperationDelay _delay;
    private readonly IOperationTelemetrySink _telemetry;
    private readonly SecretRedactor _redactor;
    private readonly Dictionary<string, CircuitState> _circuits = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public ResilientOperationExecutor(ResilientOperationOptions options, IOperationDelay delay, IOperationTelemetrySink telemetry, SecretRedactor redactor)
    { _options = options; _delay = delay; _telemetry = telemetry; _redactor = redactor; }

    public async ValueTask<T> ExecuteAsync<T>(string operation, Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        var state = GetState(operation);
        lock (_sync)
        {
            if (state.OpenedAt is { } opened && DateTimeOffset.UtcNow - opened < _options.EffectiveBreakDuration) throw new CircuitOpenException(operation);
            if (state.OpenedAt is not null) { state.OpenedAt = null; state.Failures = 0; }
        }
        Exception? last = null;
        for (var attempt = 1; attempt <= _options.MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.EffectiveTimeout);
            try
            {
                var result = await action(timeout.Token).ConfigureAwait(false);
                lock (_sync) state.Failures = 0;
                await _telemetry.WriteAsync(new(operation, attempt, "success", stopwatch.ElapsedMilliseconds, null), cancellationToken);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is TransientOperationException or TimeoutException or OperationCanceledException)
            {
                last = exception;
                await _telemetry.WriteAsync(new(operation, attempt, "transient-failure", stopwatch.ElapsedMilliseconds, _redactor.Redact(exception.Message)), CancellationToken.None);
                lock (_sync)
                {
                    state.Failures++;
                    if (state.Failures >= _options.CircuitFailureThreshold) state.OpenedAt = DateTimeOffset.UtcNow;
                }
                if (attempt < _options.MaximumAttempts && state.OpenedAt is null)
                    await _delay.DelayAsync(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1)), cancellationToken);
                else break;
            }
            catch (Exception exception)
            {
                await _telemetry.WriteAsync(new(operation, attempt, "permanent-failure", stopwatch.ElapsedMilliseconds, _redactor.Redact(exception.Message)), CancellationToken.None);
                throw;
            }
        }
        throw last ?? new InvalidOperationException("Operation failed without an exception.");
    }

    private CircuitState GetState(string operation) { lock (_sync) return _circuits.TryGetValue(operation, out var state) ? state : _circuits[operation] = new(); }
    private sealed class CircuitState { public int Failures; public DateTimeOffset? OpenedAt; }
}

public sealed class ProcessLeaseRegistry
{
    private readonly HashSet<int> _active = [];
    private readonly object _sync = new();
    public IDisposable Register(int processId) { lock (_sync) _active.Add(processId); return new Lease(this, processId); }
    public IReadOnlyList<int> Snapshot() { lock (_sync) return _active.Order().ToArray(); }
    private sealed class Lease(ProcessLeaseRegistry owner, int id) : IDisposable { public void Dispose() { lock (owner._sync) owner._active.Remove(id); } }
}

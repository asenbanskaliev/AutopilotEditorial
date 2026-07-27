using System.Collections.Concurrent;
using System.Security.Cryptography;
using BookStudio.Application.OpenCode;

namespace BookStudio.OpenCode;

/// <summary>Bounded process-lifetime idempotency ledger for OpenCode mutation commands.</summary>
internal sealed class OpenCodeSessionIdempotencyLedger
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _reservationGate = new();
    private readonly int _maximumEntries;

    public OpenCodeSessionIdempotencyLedger(int maximumEntries)
    {
        if (maximumEntries is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }
        _maximumEntries = maximumEntries;
    }

    public async ValueTask<T> ExecuteAsync<T>(
        string operation,
        string idempotencyKey,
        ReadOnlyMemory<byte> canonicalCommand,
        Func<CancellationToken, Task<T>> operationFactory,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(operationFactory);
        cancellationToken.ThrowIfCancellationRequested();

        var ledgerKey = operation + ":" + idempotencyKey;
        var fingerprint = Convert.ToHexString(SHA256.HashData(canonicalCommand.Span));
        Entry entry;
        var isOwner = false;

        lock (_reservationGate)
        {
            if (_entries.TryGetValue(ledgerKey, out entry!))
            {
                if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new OpenCodeSessionLifecycleException(
                        OpenCodeSessionErrorCodes.IdempotencyConflict);
                }
            }
            else
            {
                if (_entries.Count >= _maximumEntries)
                {
                    throw new OpenCodeSessionLifecycleException(
                        OpenCodeSessionErrorCodes.IdempotencyCapacityExceeded);
                }
                entry = new Entry(fingerprint);
                if (!_entries.TryAdd(ledgerKey, entry))
                {
                    throw new InvalidOperationException("OpenCode idempotency reservation race.");
                }
                isOwner = true;
            }
        }

        if (isOwner)
        {
            try
            {
                var result = await operationFactory(cancellationToken).ConfigureAwait(false);
                entry.Completion.TrySetResult(result);
            }
            catch (OperationCanceledException exception)
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(ledgerKey, entry));
                entry.Completion.TrySetException(exception);
            }
            catch (Exception exception)
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(ledgerKey, entry));
                entry.Completion.TrySetException(exception);
            }
        }

        var completed = await entry.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return completed is T typed
            ? typed
            : throw new InvalidOperationException("OpenCode idempotency result type mismatch.");
    }

    private sealed class Entry
    {
        public Entry(string fingerprint)
        {
            Fingerprint = fingerprint;
            Completion = new TaskCompletionSource<object>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public string Fingerprint { get; }

        public TaskCompletionSource<object> Completion { get; }
    }
}

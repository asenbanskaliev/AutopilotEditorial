namespace BookStudio.Application.Outbox;

/// <summary>Durable at-least-once Outbox lifecycle.</summary>
public interface IOutboxStore : IAsyncDisposable
{
    ValueTask<OutboxEnqueueResult> EnqueueAsync(
        OutboxMessageDraft draft,
        DateTimeOffset enqueuedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<OutboxMessage>> ClaimAsync(
        string workerId,
        int maximumMessages,
        TimeSpan leaseDuration,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    ValueTask CompleteAsync(
        Guid messageId,
        string workerId,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask FailAsync(
        Guid messageId,
        string workerId,
        string error,
        DateTimeOffset failedAtUtc,
        DateTimeOffset nextAttemptAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<OutboxMessage?> GetAsync(
        Guid messageId,
        CancellationToken cancellationToken = default);
}

public class OutboxException : Exception
{
    public OutboxException(string message) : base(message) { }
}

public sealed class OutboxMessageConflictException : OutboxException
{
    public OutboxMessageConflictException(Guid messageId)
        : base($"Outbox message '{messageId}' already exists with different immutable content.") { }
}

public sealed class OutboxLeaseException : OutboxException
{
    public OutboxLeaseException(Guid messageId, string workerId)
        : base($"Worker '{workerId}' does not own a live lease for Outbox message '{messageId}'.") { }
}

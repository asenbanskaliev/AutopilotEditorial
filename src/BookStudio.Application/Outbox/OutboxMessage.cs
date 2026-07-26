namespace BookStudio.Application.Outbox;

public enum OutboxMessageStatus
{
    Pending,
    Processing,
    Failed,
    Processed,
}

/// <summary>Durable Outbox state visible to dispatchers and operations.</summary>
public sealed record OutboxMessage(
    Guid MessageId,
    string EventType,
    string SchemaVersion,
    string PayloadJson,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset AvailableAtUtc,
    OutboxMessageStatus Status,
    int Attempts,
    string? LockedBy,
    DateTimeOffset? LockedUntilUtc,
    string? LastError,
    DateTimeOffset? ProcessedAtUtc,
    DateTimeOffset CreatedAtUtc);

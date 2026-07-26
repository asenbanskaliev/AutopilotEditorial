namespace BookStudio.Application.Outbox;

/// <summary>Immutable event payload prepared for durable Outbox enqueue.</summary>
public sealed record OutboxMessageDraft(
    Guid MessageId,
    string EventType,
    string SchemaVersion,
    string PayloadJson,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset AvailableAtUtc);

public enum OutboxEnqueueResult
{
    Inserted,
    AlreadyExists,
}

namespace BookStudio.Domain.Events;

/// <summary>Marker contract for immutable facts raised by the domain.</summary>
public interface IDomainEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAtUtc { get; }
}

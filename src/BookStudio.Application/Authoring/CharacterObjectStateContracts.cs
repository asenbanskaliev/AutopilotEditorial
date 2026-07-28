namespace BookStudio.Application.Authoring;

public interface ICharacterObjectStateStore
{
    ValueTask<NarrativeStateCreateResult> CreateAsync(NarrativeStateDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);
    ValueTask<NarrativeStateEntry> ActivateAsync(NarrativeStateControlCommand command, DateTimeOffset activatedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<NarrativeStateEntry> TransferAsync(ObjectTransferCommand command, DateTimeOffset transferredAtUtc, CancellationToken cancellationToken = default);
    ValueTask<NarrativeStateEntry?> GetAsync(string workspaceId, Guid stateId, CancellationToken cancellationToken = default);
}

public sealed record NarrativeStateDraft(Guid StateId, Guid ProjectId, Guid KnowledgeEntryId, Guid TransitionAuditId, Guid TransitionClosedMessageId, string WorkspaceId, NarrativeEntityKind EntityKind, string EntityKey, string Dimension, string Value, string? Location, string? Holder, string? ObjectType, bool Available, DateTimeOffset ValidFromUtc, DateTimeOffset? ValidToUtc, string Actor, string RequestFingerprint);
public sealed record NarrativeStateControlCommand(Guid RequestId, string WorkspaceId, Guid StateId, long ExpectedRevision, string Actor, string RequestFingerprint);
public sealed record ObjectTransferCommand(Guid RequestId, string WorkspaceId, Guid StateId, long ExpectedRevision, string ExpectedFromHolder, string ToHolder, string? ToLocation, string Actor, string RequestFingerprint);
public sealed record ObjectTransfer(Guid TransferId, string FromHolder, string ToHolder, string? ToLocation, string Actor, DateTimeOffset TransferredAtUtc);
public sealed record NarrativeStateEntry(Guid StateId, Guid ProjectId, Guid KnowledgeEntryId, Guid TransitionAuditId, Guid TransitionClosedMessageId, string WorkspaceId, NarrativeEntityKind EntityKind, string EntityKey, string Dimension, string Value, string? Location, string? Holder, string? ObjectType, bool Available, IReadOnlyList<ObjectTransfer> Transfers, DateTimeOffset ValidFromUtc, DateTimeOffset? ValidToUtc, string Actor, long Revision, NarrativeStateStatus Status, Guid? ActivationMessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record NarrativeStateCreateResult(NarrativeStateEntry Entry, bool Replayed);
public enum NarrativeEntityKind { Character, Object }
public enum NarrativeStateStatus { Draft, Active, Superseded, Retracted }
public sealed class NarrativeStateValidationException : Exception { public NarrativeStateValidationException(string message) : base(message) { } }
public sealed class NarrativeStateConflictException : Exception { public NarrativeStateConflictException(string message) : base(message) { } }
public sealed class NarrativeStateTransitionException : Exception { public NarrativeStateTransitionException(string message) : base(message) { } }

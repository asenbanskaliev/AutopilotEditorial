namespace BookStudio.Application.Authoring;

public interface IKnowledgeStateStore
{
    ValueTask<KnowledgeCreateResult> CreateAsync(KnowledgeDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);
    ValueTask<KnowledgeEntry> ActivateAsync(KnowledgeControlCommand command, DateTimeOffset activatedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<KnowledgeEntry> DiscloseAsync(KnowledgeDisclosureCommand command, DateTimeOffset disclosedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<KnowledgeEntry> SupersedeAsync(KnowledgeTerminalCommand command, DateTimeOffset supersededAtUtc, CancellationToken cancellationToken = default);
    ValueTask<KnowledgeEntry> RetractAsync(KnowledgeTerminalCommand command, DateTimeOffset retractedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<KnowledgeEntry?> GetAsync(string workspaceId, Guid entryId, CancellationToken cancellationToken = default);
}

public sealed record KnowledgeDraft(Guid EntryId, Guid ProjectId, Guid TransitionAuditId, Guid TransitionClosedMessageId, string WorkspaceId, KnowledgeKind Kind, string Subject, string Object, string Statement, string Evidence, IReadOnlyList<string> Knowners, IReadOnlyList<string> Excluded, DateTimeOffset ValidFromUtc, DateTimeOffset? ValidToUtc, string Actor, string RequestFingerprint);
public sealed record KnowledgeControlCommand(Guid RequestId, string WorkspaceId, Guid EntryId, long ExpectedRevision, string Actor, string RequestFingerprint);
public sealed record KnowledgeDisclosureCommand(Guid RequestId, string WorkspaceId, Guid EntryId, long ExpectedRevision, IReadOnlyList<string> AddKnowners, string Evidence, string Actor, string RequestFingerprint);
public sealed record KnowledgeTerminalCommand(Guid RequestId, string WorkspaceId, Guid EntryId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);
public sealed record KnowledgeDisclosure(Guid DisclosureId, IReadOnlyList<string> AddedKnowners, string Evidence, string Actor, DateTimeOffset DisclosedAtUtc);
public sealed record KnowledgeEntry(Guid EntryId, Guid ProjectId, Guid TransitionAuditId, Guid TransitionClosedMessageId, string WorkspaceId, KnowledgeKind Kind, string Subject, string Object, string Statement, string Evidence, IReadOnlyList<string> Knowners, IReadOnlyList<string> Excluded, IReadOnlyList<KnowledgeDisclosure> Disclosures, DateTimeOffset ValidFromUtc, DateTimeOffset? ValidToUtc, long Revision, KnowledgeStatus Status, Guid? ActivationMessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record KnowledgeCreateResult(KnowledgeEntry Entry, bool Replayed);

public enum KnowledgeKind { Fact, Belief, Secret }
public enum KnowledgeStatus { Draft, Active, Superseded, Retracted }
public sealed class KnowledgeValidationException : Exception { public KnowledgeValidationException(string message) : base(message) { } }
public sealed class KnowledgeConflictException : Exception { public KnowledgeConflictException(string message) : base(message) { } }
public sealed class KnowledgeTransitionException : Exception { public KnowledgeTransitionException(string message) : base(message) { } }

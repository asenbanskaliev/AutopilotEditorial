namespace BookStudio.Application.Authoring;

public interface ISpecificationStore
{
    ValueTask<SpecificationCreateResult> CreateAsync(SpecificationDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);
    ValueTask<BookSpecification> ReviseAsync(SpecificationRevisionCommand command, DateTimeOffset revisedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<BookSpecification> PrepareAsync(SpecificationControlCommand command, DateTimeOffset preparedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<BookSpecification> CommitAsync(SpecificationControlCommand command, DateTimeOffset committedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<SpecificationApprovalResult> ApproveAsync(SpecificationApprovalCommand command, DateTimeOffset approvedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<BookSpecification> OpenNextVersionAsync(SpecificationNextVersionCommand command, DateTimeOffset openedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<BookSpecification?> GetAsync(string workspaceId, Guid specificationId, CancellationToken cancellationToken = default);
}

public sealed record SpecificationDraft(Guid SpecificationId, Guid ProjectId, Guid ProposalId, long ProposalRevision, Guid ProposalApprovalMessageId, string WorkspaceId, string SchemaVersion, SpecificationContent Content, string Actor, string RequestFingerprint);
public sealed record SpecificationRevisionCommand(Guid RequestId, string WorkspaceId, Guid SpecificationId, long ExpectedVersion, long ExpectedRevision, SpecificationContent Content, string Actor, string Reason, string RequestFingerprint);
public sealed record SpecificationControlCommand(Guid RequestId, string WorkspaceId, Guid SpecificationId, long ExpectedVersion, long ExpectedRevision, string Actor, string RequestFingerprint);
public sealed record SpecificationApprovalCommand(Guid RequestId, string WorkspaceId, Guid SpecificationId, long ExpectedVersion, long ExpectedRevision, string Actor, string Reason, string RequestFingerprint);
public sealed record SpecificationNextVersionCommand(Guid RequestId, string WorkspaceId, Guid SpecificationId, long ExpectedVersion, SpecificationContent Content, string Actor, string Reason, string RequestFingerprint);

public sealed record SpecificationContent(string Goals, string Audience, string Scope, string Constraints, string QualityBars, string Deliverables, string AcceptanceCriteria);
public sealed record SpecificationVersion(long Version, long Revision, SpecificationStatus Status, SpecificationContent Content, string? ContentDigest, string Actor, string Reason, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record BookSpecification(Guid SpecificationId, Guid ProjectId, Guid ProposalId, long ProposalRevision, Guid ProposalApprovalMessageId, string WorkspaceId, string SchemaVersion, long CurrentVersion, IReadOnlyList<SpecificationVersion> Versions, Guid? ApprovalMessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc)
{
    public SpecificationVersion Current => Versions.Single(x => x.Version == CurrentVersion);
}
public sealed record SpecificationCreateResult(BookSpecification Specification, bool Replayed);
public sealed record SpecificationApprovalResult(BookSpecification Specification, bool Replayed, Guid ApprovalMessageId);
public enum SpecificationStatus { Draft, Prepared, Committed, Approved }
public sealed class SpecificationConflictException : Exception { public SpecificationConflictException(string message) : base(message) { } }
public sealed class SpecificationTransitionException : Exception { public SpecificationTransitionException(string message) : base(message) { } }
public sealed class SpecificationValidationException : Exception { public SpecificationValidationException(string message) : base(message) { } }

namespace BookStudio.Application.Authoring;

public interface IBookPlanStore
{
    ValueTask<BookPlanCreateResult> CreateAsync(BookPlanDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);
    ValueTask<BookPlan> ReviseAsync(BookPlanRevisionCommand command, DateTimeOffset revisedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<BookPlan> PrepareAsync(BookPlanControlCommand command, DateTimeOffset preparedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<BookPlan> CommitAsync(BookPlanControlCommand command, DateTimeOffset committedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<BookPlanApprovalResult> ApproveAsync(BookPlanApprovalCommand command, DateTimeOffset approvedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<BookPlan> OpenNextVersionAsync(BookPlanNextVersionCommand command, DateTimeOffset openedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<BookPlan?> GetAsync(string workspaceId, Guid planId, CancellationToken cancellationToken = default);
}

public sealed record BookPlanDraft(Guid PlanId, Guid ProjectId, Guid SpecificationId, long SpecificationVersion, Guid SpecificationApprovalMessageId, string WorkspaceId, string SchemaVersion, BookPlanContent Content, string Actor, string RequestFingerprint);
public sealed record BookPlanRevisionCommand(Guid RequestId, string WorkspaceId, Guid PlanId, long ExpectedVersion, long ExpectedRevision, BookPlanContent Content, string Actor, string Reason, string RequestFingerprint);
public sealed record BookPlanControlCommand(Guid RequestId, string WorkspaceId, Guid PlanId, long ExpectedVersion, long ExpectedRevision, string Actor, string RequestFingerprint);
public sealed record BookPlanApprovalCommand(Guid RequestId, string WorkspaceId, Guid PlanId, long ExpectedVersion, long ExpectedRevision, string Actor, string Reason, string RequestFingerprint);
public sealed record BookPlanNextVersionCommand(Guid RequestId, string WorkspaceId, Guid PlanId, long ExpectedVersion, BookPlanContent Content, string Actor, string Reason, string RequestFingerprint);

public sealed record BookPlanContent(IReadOnlyList<BookPart> Parts, IReadOnlyList<BookChapter> Chapters, IReadOnlyList<string> GlobalConstraints, IReadOnlyList<string> AcceptanceCriteria);
public sealed record BookPart(string Key, int Order, string Title, string Objective);
public sealed record BookChapter(string Key, string PartKey, int Order, string Title, string Objective, string Audience, IReadOnlyList<string> Deliverables, IReadOnlyList<string> Constraints, IReadOnlyList<string> AcceptanceCriteria, IReadOnlyList<string> DependsOn);
public sealed record BookPlanVersion(long Version, long Revision, BookPlanStatus Status, BookPlanContent Content, string? ContentDigest, string Actor, string Reason, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record BookPlan(Guid PlanId, Guid ProjectId, Guid SpecificationId, long SpecificationVersion, Guid SpecificationApprovalMessageId, string WorkspaceId, string SchemaVersion, long CurrentVersion, IReadOnlyList<BookPlanVersion> Versions, Guid? ApprovalMessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc)
{
    public BookPlanVersion Current => Versions.Single(x => x.Version == CurrentVersion);
}
public sealed record BookPlanCreateResult(BookPlan Plan, bool Replayed);
public sealed record BookPlanApprovalResult(BookPlan Plan, bool Replayed, Guid ApprovalMessageId);
public enum BookPlanStatus { Draft, Prepared, Committed, Approved }
public sealed class BookPlanConflictException : Exception { public BookPlanConflictException(string message) : base(message) { } }
public sealed class BookPlanTransitionException : Exception { public BookPlanTransitionException(string message) : base(message) { } }
public sealed class BookPlanValidationException : Exception { public BookPlanValidationException(string message) : base(message) { } }

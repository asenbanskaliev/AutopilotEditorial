namespace BookStudio.Application.Authoring;

public interface IScenePlanStore
{
    ValueTask<ScenePlanCreateResult> CreateAsync(ScenePlanDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);
    ValueTask<ScenePlan> ReviseAsync(ScenePlanRevisionCommand command, DateTimeOffset revisedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<ScenePlan> PrepareAsync(ScenePlanControlCommand command, DateTimeOffset preparedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<ScenePlan> CommitAsync(ScenePlanControlCommand command, DateTimeOffset committedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<ScenePlanApprovalResult> ApproveAsync(ScenePlanApprovalCommand command, DateTimeOffset approvedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<ScenePlan> OpenNextVersionAsync(ScenePlanNextVersionCommand command, DateTimeOffset openedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<ScenePlan?> GetAsync(string workspaceId, Guid scenePlanId, CancellationToken cancellationToken = default);
}

public sealed record ScenePlanDraft(Guid ScenePlanId, Guid ProjectId, Guid BookPlanId, long BookPlanVersion, Guid BookPlanApprovalMessageId, string BookPlanContentDigest, string WorkspaceId, string SchemaVersion, ScenePlanContent Content, string Actor, string RequestFingerprint);
public sealed record ScenePlanRevisionCommand(Guid RequestId, string WorkspaceId, Guid ScenePlanId, long ExpectedVersion, long ExpectedRevision, ScenePlanContent Content, string Actor, string Reason, string RequestFingerprint);
public sealed record ScenePlanControlCommand(Guid RequestId, string WorkspaceId, Guid ScenePlanId, long ExpectedVersion, long ExpectedRevision, string Actor, string RequestFingerprint);
public sealed record ScenePlanApprovalCommand(Guid RequestId, string WorkspaceId, Guid ScenePlanId, long ExpectedVersion, long ExpectedRevision, string Actor, string Reason, string RequestFingerprint);
public sealed record ScenePlanNextVersionCommand(Guid RequestId, string WorkspaceId, Guid ScenePlanId, long ExpectedVersion, ScenePlanContent Content, string Actor, string Reason, string RequestFingerprint);

public sealed record ScenePlanContent(IReadOnlyList<PlannedScene> Scenes, IReadOnlyList<string> GlobalConstraints, IReadOnlyList<string> AcceptanceCriteria);
public sealed record PlannedScene(string Key, string ChapterKey, int Order, string Title, string Purpose, string Summary, IReadOnlyList<string> Beats, IReadOnlyList<string> RequiredEvidence, IReadOnlyList<string> Constraints, IReadOnlyList<string> AcceptanceCriteria, IReadOnlyList<string> DependsOn);
public sealed record ScenePlanVersion(long Version, long Revision, ScenePlanStatus Status, ScenePlanContent Content, string? ContentDigest, string Actor, string Reason, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record ScenePlan(Guid ScenePlanId, Guid ProjectId, Guid BookPlanId, long BookPlanVersion, Guid BookPlanApprovalMessageId, string BookPlanContentDigest, string WorkspaceId, string SchemaVersion, long CurrentVersion, IReadOnlyList<ScenePlanVersion> Versions, Guid? ApprovalMessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc)
{
    public ScenePlanVersion Current => Versions.Single(x => x.Version == CurrentVersion);
}
public sealed record ScenePlanCreateResult(ScenePlan ScenePlan, bool Replayed);
public sealed record ScenePlanApprovalResult(ScenePlan ScenePlan, bool Replayed, Guid ApprovalMessageId);
public enum ScenePlanStatus { Draft, Prepared, Committed, Approved }
public sealed class ScenePlanConflictException : Exception { public ScenePlanConflictException(string message) : base(message) { } }
public sealed class ScenePlanTransitionException : Exception { public ScenePlanTransitionException(string message) : base(message) { } }
public sealed class ScenePlanValidationException : Exception { public ScenePlanValidationException(string message) : base(message) { } }
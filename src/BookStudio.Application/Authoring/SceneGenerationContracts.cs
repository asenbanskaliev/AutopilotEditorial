namespace BookStudio.Application.Authoring;

public interface ISceneGenerationStore
{
    ValueTask<SceneGenerationCreateResult> CreateAsync(SceneGenerationDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);
    ValueTask<SceneGeneration> StartAttemptAsync(SceneGenerationStartCommand command, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<SceneGeneration> CompleteAttemptAsync(SceneGenerationCompleteCommand command, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<SceneGeneration> FailAttemptAsync(SceneGenerationFailCommand command, DateTimeOffset failedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<SceneGeneration> SubmitAsync(SceneGenerationSubmitCommand command, DateTimeOffset submittedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<SceneGenerationApprovalResult> ApproveAsync(SceneGenerationApprovalCommand command, DateTimeOffset approvedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<SceneGeneration?> GetAsync(string workspaceId, Guid generationId, CancellationToken cancellationToken = default);
}

public sealed record SceneGenerationDraft(Guid GenerationId, Guid ProjectId, Guid ScenePlanId, long ScenePlanVersion, Guid ScenePlanApprovalMessageId, string ScenePlanContentDigest, string WorkspaceId, string SchemaVersion, SceneGenerationBrief Brief, string Actor, string RequestFingerprint);
public sealed record SceneGenerationStartCommand(Guid RequestId, string WorkspaceId, Guid GenerationId, long ExpectedRevision, SceneInvocation Invocation, string Actor, string RequestFingerprint);
public sealed record SceneGenerationCompleteCommand(Guid RequestId, string WorkspaceId, Guid GenerationId, long ExpectedRevision, long ExpectedAttempt, string GeneratedText, IReadOnlyList<AcceptanceEvidence> AcceptanceEvidence, string Actor, string RequestFingerprint);
public sealed record SceneGenerationFailCommand(Guid RequestId, string WorkspaceId, Guid GenerationId, long ExpectedRevision, long ExpectedAttempt, string ErrorClass, string ErrorText, bool Retryable, string Actor, string RequestFingerprint);
public sealed record SceneGenerationSubmitCommand(Guid RequestId, string WorkspaceId, Guid GenerationId, long ExpectedRevision, string Actor, string RequestFingerprint);
public sealed record SceneGenerationApprovalCommand(Guid RequestId, string WorkspaceId, Guid GenerationId, long ExpectedRevision, string Actor, string Reason, string RequestFingerprint);

public sealed record SceneGenerationBrief(string SceneKey, string ChapterKey, int Order, string Title, string Purpose, string Summary, IReadOnlyList<string> Beats, IReadOnlyList<string> RequiredEvidence, IReadOnlyList<string> Constraints, IReadOnlyList<string> AcceptanceCriteria);
public sealed record SceneInvocation(string Provider, string Model, string PromptTemplateVersion, string CompiledContextDigest, string ParametersJson, string PolicyProfile);
public sealed record AcceptanceEvidence(string Criterion, string Evidence);
public sealed record SceneGenerationAttempt(long Attempt, SceneAttemptStatus Status, SceneInvocation Invocation, string? GeneratedText, string? ContentDigest, IReadOnlyList<AcceptanceEvidence> AcceptanceEvidence, string? ErrorClass, string? ErrorText, bool? Retryable, string Actor, DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc);
public sealed record SceneGeneration(Guid GenerationId, Guid ProjectId, Guid ScenePlanId, long ScenePlanVersion, Guid ScenePlanApprovalMessageId, string ScenePlanContentDigest, string WorkspaceId, string SchemaVersion, SceneGenerationBrief Brief, long Revision, SceneGenerationStatus Status, IReadOnlyList<SceneGenerationAttempt> Attempts, Guid? ApprovalMessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record SceneGenerationCreateResult(SceneGeneration Generation, bool Replayed);
public sealed record SceneGenerationApprovalResult(SceneGeneration Generation, bool Replayed, Guid ApprovalMessageId);
public enum SceneGenerationStatus { Planned, Generating, Generated, Failed, Submitted, Approved }
public enum SceneAttemptStatus { Running, Generated, Failed }
public sealed class SceneGenerationConflictException : Exception { public SceneGenerationConflictException(string message) : base(message) { } }
public sealed class SceneGenerationTransitionException : Exception { public SceneGenerationTransitionException(string message) : base(message) { } }
public sealed class SceneGenerationValidationException : Exception { public SceneGenerationValidationException(string message) : base(message) { } }

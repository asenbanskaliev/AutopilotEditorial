namespace BookStudio.Application.Authoring;

public interface IEditorialPassPlanStore
{
    ValueTask<EditorialPassPlanCreateResult> CreateAsync(EditorialPassPlanDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<EditorialPassPlan> StartPassAsync(EditorialPassCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<EditorialPassPlan> RecordGateAsync(EditorialPassGateCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<EditorialPassPlan> CompletePassAsync(EditorialPassCompleteCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<EditorialPassPlan> BlockPassAsync(EditorialPassBlockCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<EditorialPassPlan> MarkStaleAsync(EditorialPassStaleCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<EditorialPassPlan?> GetAsync(string workspaceId, Guid planId, CancellationToken ct = default);
}

public sealed record EditorialPassPlanDraft(
    Guid PlanId,
    Guid ProjectId,
    string WorkspaceId,
    Guid CrossChapterAuditId,
    long ExpectedAuditRevision,
    string ExpectedAuditDigest,
    int Version,
    string Actor,
    string RequestFingerprint);

public sealed record EditorialPassCommand(
    Guid RequestId,
    string WorkspaceId,
    Guid PlanId,
    long ExpectedRevision,
    EditorialPassKind Pass,
    string Actor,
    string RequestFingerprint);

public sealed record EditorialPassGateCommand(
    Guid RequestId,
    string WorkspaceId,
    Guid PlanId,
    long ExpectedRevision,
    EditorialPassKind Pass,
    EditorialGateResult Result,
    string Evidence,
    string Actor,
    string RequestFingerprint);

public sealed record EditorialPassCompleteCommand(
    Guid RequestId,
    string WorkspaceId,
    Guid PlanId,
    long ExpectedRevision,
    EditorialPassKind Pass,
    string Result,
    string Evidence,
    string Actor,
    string RequestFingerprint);

public sealed record EditorialPassBlockCommand(
    Guid RequestId,
    string WorkspaceId,
    Guid PlanId,
    long ExpectedRevision,
    EditorialPassKind Pass,
    string Reason,
    string Actor,
    string RequestFingerprint);

public sealed record EditorialPassStaleCommand(
    Guid RequestId,
    string WorkspaceId,
    Guid PlanId,
    long ExpectedRevision,
    string Reason,
    string Actor,
    string RequestFingerprint);

public sealed record EditorialPassNode(
    EditorialPassKind Pass,
    IReadOnlyList<EditorialPassKind> Dependencies,
    EditorialPassStatus Status,
    int Attempts,
    EditorialGateResult? Gate,
    string? Evidence,
    string? Result,
    string? Responsible,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record EditorialPassPlan(
    Guid PlanId,
    Guid ProjectId,
    string WorkspaceId,
    Guid CrossChapterAuditId,
    long ExpectedAuditRevision,
    string ExpectedAuditDigest,
    int Version,
    string Actor,
    long Revision,
    EditorialPlanStatus Status,
    IReadOnlyList<EditorialPassNode> Passes,
    Guid? MessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record EditorialPassPlanCreateResult(EditorialPassPlan Plan, bool Replayed);

public enum EditorialPlanStatus { Planned, InProgress, Blocked, Completed, Stale }
public enum EditorialPassStatus { Pending, Ready, InProgress, Blocked, Completed }
public enum EditorialGateResult { Pass, Fail }
public enum EditorialPassKind
{
    Developmental,
    StructuralContent,
    VoiceLine,
    Dialogue,
    ThemesPacing,
    CopyeditProofreading,
    BetaReaders,
    OriginalityReadAloud
}

public sealed class EditorialPassValidationException : Exception
{
    public EditorialPassValidationException(string message) : base(message) { }
}

public sealed class EditorialPassConflictException : Exception
{
    public EditorialPassConflictException(string message) : base(message) { }
}

public sealed class EditorialPassTransitionException : Exception
{
    public EditorialPassTransitionException(string message) : base(message) { }
}

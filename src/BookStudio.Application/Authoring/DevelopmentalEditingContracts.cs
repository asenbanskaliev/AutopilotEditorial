namespace BookStudio.Application.Authoring;

public interface IDevelopmentalEditingStore
{
    ValueTask<DevelopmentalReviewCreateResult> CreateAsync(DevelopmentalReviewDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<DevelopmentalReview> EvaluateAsync(DevelopmentalEvaluateCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<DevelopmentalReview> DecideAsync(DevelopmentalDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<DevelopmentalReview> ReopenAsync(DevelopmentalReopenCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<DevelopmentalReview> MarkStaleAsync(DevelopmentalStaleCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<DevelopmentalReview?> GetAsync(string workspaceId, Guid reviewId, CancellationToken ct = default);
}

public sealed record DevelopmentalReviewDraft(
    Guid ReviewId,
    Guid ProjectId,
    string WorkspaceId,
    Guid EditorialPlanId,
    long ExpectedPlanRevision,
    string ExpectedPlanDigest,
    int Version,
    string RuleSet,
    string Actor,
    string SnapshotJson,
    string RequestFingerprint);

public sealed record DevelopmentalEvaluateCommand(
    Guid RequestId,
    string WorkspaceId,
    Guid ReviewId,
    long ExpectedRevision,
    IReadOnlyList<DevelopmentalFindingDraft> Findings,
    string Evidence,
    string Actor,
    string RequestFingerprint);

public sealed record DevelopmentalDecisionCommand(
    Guid RequestId,
    string WorkspaceId,
    Guid ReviewId,
    long ExpectedRevision,
    DevelopmentalDecision Decision,
    string Reason,
    int? ExpectedRepairRevision,
    string Actor,
    string RequestFingerprint);

public sealed record DevelopmentalReopenCommand(
    Guid RequestId,
    string WorkspaceId,
    Guid ReviewId,
    long ExpectedRevision,
    string Reason,
    string Actor,
    string RequestFingerprint);

public sealed record DevelopmentalStaleCommand(
    Guid RequestId,
    string WorkspaceId,
    Guid ReviewId,
    long ExpectedRevision,
    string Reason,
    string Actor,
    string RequestFingerprint);

public sealed record DevelopmentalFindingDraft(
    Guid FindingId,
    DevelopmentalFindingArea Area,
    DevelopmentalSeverity Severity,
    string Rule,
    string Location,
    IReadOnlyList<int> ChapterNumbers,
    string Evidence,
    bool IsOpen = true);

public sealed record DevelopmentalFinding(
    Guid FindingId,
    DevelopmentalFindingArea Area,
    DevelopmentalSeverity Severity,
    string Rule,
    string Location,
    IReadOnlyList<int> ChapterNumbers,
    string Evidence,
    bool IsOpen);

public sealed record DevelopmentalReview(
    Guid ReviewId,
    Guid ProjectId,
    string WorkspaceId,
    Guid EditorialPlanId,
    long ExpectedPlanRevision,
    string ExpectedPlanDigest,
    int Version,
    string RuleSet,
    string Actor,
    string SnapshotJson,
    long Revision,
    DevelopmentalReviewStatus Status,
    IReadOnlyList<DevelopmentalFinding> Findings,
    DevelopmentalDecision? Decision,
    string? DecisionReason,
    int? ExpectedRepairRevision,
    Guid? MessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record DevelopmentalReviewCreateResult(DevelopmentalReview Review, bool Replayed);

public enum DevelopmentalReviewStatus { Proposed, Evaluated, Approved, Rejected, RepairRequired, Stale }
public enum DevelopmentalDecision { Approve, Reject, ReturnToRepair }
public enum DevelopmentalSeverity { Info, Minor, Major, Blocking }
public enum DevelopmentalFindingArea { EditorialPromise, GlobalStructure, Scope, CharacterArc, Progression, MacroRedundancy, ContentGap }

public sealed class DevelopmentalEditingValidationException : Exception
{
    public DevelopmentalEditingValidationException(string message) : base(message) { }
}

public sealed class DevelopmentalEditingConflictException : Exception
{
    public DevelopmentalEditingConflictException(string message) : base(message) { }
}

public sealed class DevelopmentalEditingTransitionException : Exception
{
    public DevelopmentalEditingTransitionException(string message) : base(message) { }
}

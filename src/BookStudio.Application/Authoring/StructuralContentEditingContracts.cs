namespace BookStudio.Application.Authoring;

public interface IStructuralContentEditingStore
{
    ValueTask<StructuralContentReviewCreateResult> CreateAsync(StructuralContentReviewDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<StructuralContentReview> EvaluateAsync(StructuralContentEvaluateCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<StructuralContentReview> DecideAsync(StructuralContentDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<StructuralContentReview> ReopenAsync(StructuralContentReopenCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<StructuralContentReview> MarkStaleAsync(StructuralContentStaleCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<StructuralContentReview?> GetAsync(string workspaceId, Guid reviewId, CancellationToken ct = default);
}

public sealed record StructuralContentReviewDraft(
    Guid ReviewId,
    Guid ProjectId,
    string WorkspaceId,
    Guid EditorialPlanId,
    Guid DevelopmentalReviewId,
    long ExpectedDevelopmentalRevision,
    string ExpectedDevelopmentalDigest,
    int Version,
    string RuleSet,
    string Actor,
    string SnapshotJson,
    string RequestFingerprint);

public sealed record StructuralContentEvaluateCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, IReadOnlyList<StructuralContentFindingDraft> Findings, string Evidence, string Actor, string RequestFingerprint);
public sealed record StructuralContentDecisionCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, StructuralContentDecision Decision, string Reason, int? ExpectedRepairRevision, string Actor, string RequestFingerprint);
public sealed record StructuralContentReopenCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);
public sealed record StructuralContentStaleCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);

public sealed record StructuralContentFindingDraft(Guid FindingId, StructuralContentFindingArea Area, StructuralContentSeverity Severity, string Rule, string Location, IReadOnlyList<int> ChapterNumbers, IReadOnlyList<string> SceneIds, string Evidence, bool IsOpen = true);
public sealed record StructuralContentFinding(Guid FindingId, StructuralContentFindingArea Area, StructuralContentSeverity Severity, string Rule, string Location, IReadOnlyList<int> ChapterNumbers, IReadOnlyList<string> SceneIds, string Evidence, bool IsOpen);

public sealed record StructuralContentReview(
    Guid ReviewId,
    Guid ProjectId,
    string WorkspaceId,
    Guid EditorialPlanId,
    Guid DevelopmentalReviewId,
    long ExpectedDevelopmentalRevision,
    string ExpectedDevelopmentalDigest,
    int Version,
    string RuleSet,
    string Actor,
    string SnapshotJson,
    long Revision,
    StructuralContentReviewStatus Status,
    IReadOnlyList<StructuralContentFinding> Findings,
    StructuralContentDecision? Decision,
    string? DecisionReason,
    int? ExpectedRepairRevision,
    Guid? MessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record StructuralContentReviewCreateResult(StructuralContentReview Review, bool Replayed);

public enum StructuralContentReviewStatus { Proposed, Evaluated, Approved, Rejected, RepairRequired, Stale }
public enum StructuralContentDecision { Approve, Reject, ReturnToRepair }
public enum StructuralContentSeverity { Info, Minor, Major, Blocking }
public enum StructuralContentFindingArea { ChapterOrder, SceneOrder, TreatmentDepth, Continuity, ObjectiveCoverage, Redundancy, ContentGap, OutOfScope }

public sealed class StructuralContentEditingValidationException : Exception { public StructuralContentEditingValidationException(string message) : base(message) { } }
public sealed class StructuralContentEditingConflictException : Exception { public StructuralContentEditingConflictException(string message) : base(message) { } }
public sealed class StructuralContentEditingTransitionException : Exception { public StructuralContentEditingTransitionException(string message) : base(message) { } }

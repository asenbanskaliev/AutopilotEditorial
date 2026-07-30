namespace BookStudio.Application.Authoring;

public interface IBetaReaderReviewStore
{
    ValueTask<BetaReaderReviewCreateResult> CreateAsync(BetaReaderReviewDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<BetaReaderReview> EvaluateAsync(BetaReaderEvaluateCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<BetaReaderReview> DecideAsync(BetaReaderDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<BetaReaderReview> ReopenAsync(BetaReaderReopenCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<BetaReaderReview> MarkStaleAsync(BetaReaderStaleCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<BetaReaderReview?> GetAsync(string workspaceId, Guid reviewId, CancellationToken ct = default);
}

public sealed record BetaReaderReviewDraft(Guid ReviewId, Guid ProjectId, string WorkspaceId, Guid EditorialPlanId, Guid CopyeditProofreadingReviewId, long ExpectedCopyeditProofreadingRevision, string ExpectedCopyeditProofreadingDigest, int Version, string ReaderProfile, string RuleSet, string Actor, string SnapshotJson, string RequestFingerprint);
public sealed record BetaReaderEvaluateCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, IReadOnlyList<BetaReaderFindingDraft> Findings, string Evidence, string Actor, string RequestFingerprint);
public sealed record BetaReaderDecisionCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, BetaReaderDecision Decision, string Reason, int? ExpectedRepairRevision, string Actor, string RequestFingerprint);
public sealed record BetaReaderReopenCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);
public sealed record BetaReaderStaleCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);

public sealed record BetaReaderFindingDraft(Guid FindingId, BetaReaderFindingArea Area, BetaReaderSeverity Severity, string Rule, string Location, IReadOnlyList<int> ChapterNumbers, IReadOnlyList<string> SceneIds, IReadOnlyList<string> ParagraphIds, string ReaderObservation, string Evidence, bool IsOpen = true);
public sealed record BetaReaderFinding(Guid FindingId, BetaReaderFindingArea Area, BetaReaderSeverity Severity, string Rule, string Location, IReadOnlyList<int> ChapterNumbers, IReadOnlyList<string> SceneIds, IReadOnlyList<string> ParagraphIds, string ReaderObservation, string Evidence, bool IsOpen);

public sealed record BetaReaderReview(Guid ReviewId, Guid ProjectId, string WorkspaceId, Guid EditorialPlanId, Guid CopyeditProofreadingReviewId, long ExpectedCopyeditProofreadingRevision, string ExpectedCopyeditProofreadingDigest, int Version, string ReaderProfile, string RuleSet, string Actor, string SnapshotJson, long Revision, BetaReaderReviewStatus Status, IReadOnlyList<BetaReaderFinding> Findings, BetaReaderDecision? Decision, string? DecisionReason, int? ExpectedRepairRevision, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record BetaReaderReviewCreateResult(BetaReaderReview Review, bool Replayed);

public enum BetaReaderReviewStatus { Proposed, Evaluated, Approved, Rejected, RepairRequired, Stale }
public enum BetaReaderDecision { Approve, Reject, ReturnToRepair }
public enum BetaReaderSeverity { Info, Minor, Major, Blocking }
public enum BetaReaderFindingArea { Comprehension, Engagement, EmotionalImpact, CharacterCredibility, PlotClarity, Pacing, Confusion, ExpectationMismatch, Satisfaction, Accessibility, AudienceFit, Sensitivity }

public sealed class BetaReaderValidationException : Exception { public BetaReaderValidationException(string message) : base(message) { } }
public sealed class BetaReaderConflictException : Exception { public BetaReaderConflictException(string message) : base(message) { } }
public sealed class BetaReaderTransitionException : Exception { public BetaReaderTransitionException(string message) : base(message) { } }

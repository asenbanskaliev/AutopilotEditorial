namespace BookStudio.Application.Authoring;

public interface IOriginalityReadAloudReviewStore
{
    ValueTask<OriginalityReadAloudReviewCreateResult> CreateAsync(OriginalityReadAloudReviewDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<OriginalityReadAloudReview> EvaluateAsync(OriginalityReadAloudEvaluateCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<OriginalityReadAloudReview> DecideAsync(OriginalityReadAloudDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<OriginalityReadAloudReview> ReopenAsync(OriginalityReadAloudReopenCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<OriginalityReadAloudReview> MarkStaleAsync(OriginalityReadAloudStaleCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<OriginalityReadAloudReview?> GetAsync(string workspaceId, Guid reviewId, CancellationToken ct = default);
}

public sealed record OriginalityReadAloudReviewDraft(Guid ReviewId, Guid ProjectId, string WorkspaceId, Guid EditorialPlanId, Guid BetaReaderReviewId, long ExpectedBetaReaderRevision, string ExpectedBetaReaderDigest, int Version, string RuleSet, string Actor, string SnapshotJson, string RequestFingerprint);
public sealed record OriginalityReadAloudEvaluateCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, IReadOnlyList<OriginalityReadAloudFindingDraft> Findings, string Evidence, string Actor, string RequestFingerprint);
public sealed record OriginalityReadAloudDecisionCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, OriginalityReadAloudDecision Decision, string Reason, int? ExpectedRepairRevision, string Actor, string RequestFingerprint);
public sealed record OriginalityReadAloudReopenCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);
public sealed record OriginalityReadAloudStaleCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);

public sealed record OriginalityReadAloudFindingDraft(Guid FindingId, OriginalityReadAloudFindingArea Area, OriginalityReadAloudSeverity Severity, string Rule, string Location, IReadOnlyList<int> ChapterNumbers, IReadOnlyList<string> SceneIds, IReadOnlyList<string> ParagraphIds, IReadOnlyList<string> SpanIds, string Observation, string Evidence, bool IsOpen = true);
public sealed record OriginalityReadAloudFinding(Guid FindingId, OriginalityReadAloudFindingArea Area, OriginalityReadAloudSeverity Severity, string Rule, string Location, IReadOnlyList<int> ChapterNumbers, IReadOnlyList<string> SceneIds, IReadOnlyList<string> ParagraphIds, IReadOnlyList<string> SpanIds, string Observation, string Evidence, bool IsOpen);

public sealed record OriginalityReadAloudReview(Guid ReviewId, Guid ProjectId, string WorkspaceId, Guid EditorialPlanId, Guid BetaReaderReviewId, long ExpectedBetaReaderRevision, string ExpectedBetaReaderDigest, int Version, string RuleSet, string Actor, string SnapshotJson, long Revision, OriginalityReadAloudReviewStatus Status, IReadOnlyList<OriginalityReadAloudFinding> Findings, OriginalityReadAloudDecision? Decision, string? DecisionReason, int? ExpectedRepairRevision, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record OriginalityReadAloudReviewCreateResult(OriginalityReadAloudReview Review, bool Replayed);

public enum OriginalityReadAloudReviewStatus { Proposed, Evaluated, Approved, Rejected, RepairRequired, Stale }
public enum OriginalityReadAloudDecision { Approve, Reject, ReturnToRepair }
public enum OriginalityReadAloudSeverity { Info, Minor, Major, Blocking }
public enum OriginalityReadAloudFindingArea { Originality, FormulaicLanguage, Cliche, UnintendedSimilarity, AttributionRisk, ReadAloudFlow, Pronunciation, Cadence, Breath, TongueTwister, Ambiguity, Repetition, Accessibility }

public sealed class OriginalityReadAloudValidationException : Exception { public OriginalityReadAloudValidationException(string message) : base(message) { } }
public sealed class OriginalityReadAloudConflictException : Exception { public OriginalityReadAloudConflictException(string message) : base(message) { } }
public sealed class OriginalityReadAloudTransitionException : Exception { public OriginalityReadAloudTransitionException(string message) : base(message) { } }

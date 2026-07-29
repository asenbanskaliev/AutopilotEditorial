namespace BookStudio.Application.Authoring;

public interface IThemesPacingEditingStore
{
    ValueTask<ThemesPacingReviewCreateResult> CreateAsync(ThemesPacingReviewDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ThemesPacingReview> EvaluateAsync(ThemesPacingEvaluateCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ThemesPacingReview> DecideAsync(ThemesPacingDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ThemesPacingReview> ReopenAsync(ThemesPacingReopenCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ThemesPacingReview> MarkStaleAsync(ThemesPacingStaleCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ThemesPacingReview?> GetAsync(string workspaceId, Guid reviewId, CancellationToken ct = default);
}

public sealed record ThemesPacingReviewDraft(Guid ReviewId, Guid ProjectId, string WorkspaceId, Guid EditorialPlanId, Guid DialogueReviewId, long ExpectedDialogueRevision, string ExpectedDialogueDigest, int Version, string RuleSet, string Actor, string SnapshotJson, string RequestFingerprint);
public sealed record ThemesPacingEvaluateCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, IReadOnlyList<ThemesPacingFindingDraft> Findings, string Evidence, string Actor, string RequestFingerprint);
public sealed record ThemesPacingDecisionCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, ThemesPacingDecision Decision, string Reason, int? ExpectedRepairRevision, string Actor, string RequestFingerprint);
public sealed record ThemesPacingReopenCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);
public sealed record ThemesPacingStaleCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);

public sealed record ThemesPacingFindingDraft(Guid FindingId, ThemesPacingFindingArea Area, ThemesPacingSeverity Severity, string Rule, string Location, IReadOnlyList<int> ChapterNumbers, IReadOnlyList<string> SceneIds, IReadOnlyList<string> BeatIds, IReadOnlyList<string> Spans, string Evidence, bool IsOpen = true);
public sealed record ThemesPacingFinding(Guid FindingId, ThemesPacingFindingArea Area, ThemesPacingSeverity Severity, string Rule, string Location, IReadOnlyList<int> ChapterNumbers, IReadOnlyList<string> SceneIds, IReadOnlyList<string> BeatIds, IReadOnlyList<string> Spans, string Evidence, bool IsOpen);

public sealed record ThemesPacingReview(Guid ReviewId, Guid ProjectId, string WorkspaceId, Guid EditorialPlanId, Guid DialogueReviewId, long ExpectedDialogueRevision, string ExpectedDialogueDigest, int Version, string RuleSet, string Actor, string SnapshotJson, long Revision, ThemesPacingReviewStatus Status, IReadOnlyList<ThemesPacingFinding> Findings, ThemesPacingDecision? Decision, string? DecisionReason, int? ExpectedRepairRevision, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record ThemesPacingReviewCreateResult(ThemesPacingReview Review, bool Replayed);

public enum ThemesPacingReviewStatus { Proposed, Evaluated, Approved, Rejected, RepairRequired, Stale }
public enum ThemesPacingDecision { Approve, Reject, ReturnToRepair }
public enum ThemesPacingSeverity { Info, Minor, Major, Blocking }
public enum ThemesPacingFindingArea { ThemeClarity, ThemeProgression, ThemePayoff, Pacing, TensionCurve, SceneWeight, Repetition, Momentum }

public sealed class ThemesPacingEditingValidationException : Exception { public ThemesPacingEditingValidationException(string message) : base(message) { } }
public sealed class ThemesPacingEditingConflictException : Exception { public ThemesPacingEditingConflictException(string message) : base(message) { } }
public sealed class ThemesPacingEditingTransitionException : Exception { public ThemesPacingEditingTransitionException(string message) : base(message) { } }

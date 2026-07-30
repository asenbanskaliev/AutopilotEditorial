namespace BookStudio.Application.Authoring;

public interface ICopyeditProofreadingStore
{
    ValueTask<CopyeditProofreadingReviewCreateResult> CreateAsync(CopyeditProofreadingReviewDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<CopyeditProofreadingReview> EvaluateAsync(CopyeditProofreadingEvaluateCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<CopyeditProofreadingReview> DecideAsync(CopyeditProofreadingDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<CopyeditProofreadingReview> ReopenAsync(CopyeditProofreadingReopenCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<CopyeditProofreadingReview> MarkStaleAsync(CopyeditProofreadingStaleCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<CopyeditProofreadingReview?> GetAsync(string workspaceId, Guid reviewId, CancellationToken ct = default);
}

public sealed record CopyeditProofreadingReviewDraft(Guid ReviewId, Guid ProjectId, string WorkspaceId, Guid EditorialPlanId, Guid ThemesPacingReviewId, long ExpectedThemesPacingRevision, string ExpectedThemesPacingDigest, int Version, string RuleSet, string StyleGuide, string LanguageTag, string Actor, string SnapshotJson, string RequestFingerprint);
public sealed record CopyeditProofreadingEvaluateCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, IReadOnlyList<CopyeditProofreadingFindingDraft> Findings, string Evidence, string Actor, string RequestFingerprint);
public sealed record CopyeditProofreadingDecisionCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, CopyeditProofreadingDecision Decision, string Reason, int? ExpectedRepairRevision, string Actor, string RequestFingerprint);
public sealed record CopyeditProofreadingReopenCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);
public sealed record CopyeditProofreadingStaleCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);

public sealed record CopyeditProofreadingFindingDraft(Guid FindingId, CopyeditProofreadingFindingArea Area, CopyeditProofreadingSeverity Severity, string Rule, string Location, IReadOnlyList<int> ChapterNumbers, IReadOnlyList<string> SceneIds, IReadOnlyList<string> ParagraphIds, IReadOnlyList<string> Spans, string SuggestedCorrection, string Evidence, bool IsOpen = true);
public sealed record CopyeditProofreadingFinding(Guid FindingId, CopyeditProofreadingFindingArea Area, CopyeditProofreadingSeverity Severity, string Rule, string Location, IReadOnlyList<int> ChapterNumbers, IReadOnlyList<string> SceneIds, IReadOnlyList<string> ParagraphIds, IReadOnlyList<string> Spans, string SuggestedCorrection, string Evidence, bool IsOpen);

public sealed record CopyeditProofreadingReview(Guid ReviewId, Guid ProjectId, string WorkspaceId, Guid EditorialPlanId, Guid ThemesPacingReviewId, long ExpectedThemesPacingRevision, string ExpectedThemesPacingDigest, int Version, string RuleSet, string StyleGuide, string LanguageTag, string Actor, string SnapshotJson, long Revision, CopyeditProofreadingReviewStatus Status, IReadOnlyList<CopyeditProofreadingFinding> Findings, CopyeditProofreadingDecision? Decision, string? DecisionReason, int? ExpectedRepairRevision, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record CopyeditProofreadingReviewCreateResult(CopyeditProofreadingReview Review, bool Replayed);

public enum CopyeditProofreadingReviewStatus { Proposed, Evaluated, Approved, Rejected, RepairRequired, Stale }
public enum CopyeditProofreadingDecision { Approve, Reject, ReturnToRepair }
public enum CopyeditProofreadingSeverity { Info, Minor, Major, Blocking }
public enum CopyeditProofreadingFindingArea { Grammar, Spelling, Punctuation, Syntax, Usage, Terminology, Capitalization, Hyphenation, Formatting, TypographicConsistency, FactualTypo, ProofreadingArtifact }

public sealed class CopyeditProofreadingValidationException : Exception { public CopyeditProofreadingValidationException(string message) : base(message) { } }
public sealed class CopyeditProofreadingConflictException : Exception { public CopyeditProofreadingConflictException(string message) : base(message) { } }
public sealed class CopyeditProofreadingTransitionException : Exception { public CopyeditProofreadingTransitionException(string message) : base(message) { } }

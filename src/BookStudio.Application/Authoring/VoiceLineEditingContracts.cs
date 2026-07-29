namespace BookStudio.Application.Authoring;

public interface IVoiceLineEditingStore
{
    ValueTask<VoiceLineReviewCreateResult> CreateAsync(VoiceLineReviewDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VoiceLineReview> EvaluateAsync(VoiceLineEvaluateCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VoiceLineReview> DecideAsync(VoiceLineDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VoiceLineReview> ReopenAsync(VoiceLineReopenCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VoiceLineReview> MarkStaleAsync(VoiceLineStaleCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VoiceLineReview?> GetAsync(string workspaceId, Guid reviewId, CancellationToken ct = default);
}

public sealed record VoiceLineReviewDraft(
    Guid ReviewId,
    Guid ProjectId,
    string WorkspaceId,
    Guid EditorialPlanId,
    Guid StructuralContentReviewId,
    long ExpectedStructuralContentRevision,
    string ExpectedStructuralContentDigest,
    int Version,
    string RuleSet,
    string Actor,
    string SnapshotJson,
    string RequestFingerprint);

public sealed record VoiceLineEvaluateCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, IReadOnlyList<VoiceLineFindingDraft> Findings, string Evidence, string Actor, string RequestFingerprint);
public sealed record VoiceLineDecisionCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, VoiceLineDecision Decision, string Reason, int? ExpectedRepairRevision, string Actor, string RequestFingerprint);
public sealed record VoiceLineReopenCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);
public sealed record VoiceLineStaleCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);

public sealed record VoiceLineFindingDraft(Guid FindingId, VoiceLineFindingArea Area, VoiceLineSeverity Severity, string Rule, string Location, IReadOnlyList<int> ChapterNumbers, IReadOnlyList<string> SceneIds, IReadOnlyList<string> ParagraphIds, IReadOnlyList<string> Spans, string Evidence, bool IsOpen = true);
public sealed record VoiceLineFinding(Guid FindingId, VoiceLineFindingArea Area, VoiceLineSeverity Severity, string Rule, string Location, IReadOnlyList<int> ChapterNumbers, IReadOnlyList<string> SceneIds, IReadOnlyList<string> ParagraphIds, IReadOnlyList<string> Spans, string Evidence, bool IsOpen);

public sealed record VoiceLineReview(
    Guid ReviewId,
    Guid ProjectId,
    string WorkspaceId,
    Guid EditorialPlanId,
    Guid StructuralContentReviewId,
    long ExpectedStructuralContentRevision,
    string ExpectedStructuralContentDigest,
    int Version,
    string RuleSet,
    string Actor,
    string SnapshotJson,
    long Revision,
    VoiceLineReviewStatus Status,
    IReadOnlyList<VoiceLineFinding> Findings,
    VoiceLineDecision? Decision,
    string? DecisionReason,
    int? ExpectedRepairRevision,
    Guid? MessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record VoiceLineReviewCreateResult(VoiceLineReview Review, bool Replayed);

public enum VoiceLineReviewStatus { Proposed, Evaluated, Approved, Rejected, RepairRequired, Stale }
public enum VoiceLineDecision { Approve, Reject, ReturnToRepair }
public enum VoiceLineSeverity { Info, Minor, Major, Blocking }
public enum VoiceLineFindingArea { NarrativeVoice, SentenceClarity, Rhythm, LexicalPrecision, StyleConsistency, Readability, Density }

public sealed class VoiceLineEditingValidationException : Exception { public VoiceLineEditingValidationException(string message) : base(message) { } }
public sealed class VoiceLineEditingConflictException : Exception { public VoiceLineEditingConflictException(string message) : base(message) { } }
public sealed class VoiceLineEditingTransitionException : Exception { public VoiceLineEditingTransitionException(string message) : base(message) { } }

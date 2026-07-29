namespace BookStudio.Application.Authoring;

public interface IDialogueEditingStore
{
    ValueTask<DialogueReviewCreateResult> CreateAsync(DialogueReviewDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<DialogueReview> EvaluateAsync(DialogueEvaluateCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<DialogueReview> DecideAsync(DialogueDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<DialogueReview> ReopenAsync(DialogueReopenCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<DialogueReview> MarkStaleAsync(DialogueStaleCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<DialogueReview?> GetAsync(string workspaceId, Guid reviewId, CancellationToken ct = default);
}

public sealed record DialogueReviewDraft(
    Guid ReviewId,
    Guid ProjectId,
    string WorkspaceId,
    Guid EditorialPlanId,
    Guid VoiceLineReviewId,
    long ExpectedVoiceLineRevision,
    string ExpectedVoiceLineDigest,
    int Version,
    string RuleSet,
    string Actor,
    string SnapshotJson,
    string RequestFingerprint);

public sealed record DialogueEvaluateCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, IReadOnlyList<DialogueFindingDraft> Findings, string Evidence, string Actor, string RequestFingerprint);
public sealed record DialogueDecisionCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, DialogueDecision Decision, string Reason, int? ExpectedRepairRevision, string Actor, string RequestFingerprint);
public sealed record DialogueReopenCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);
public sealed record DialogueStaleCommand(Guid RequestId, string WorkspaceId, Guid ReviewId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);

public sealed record DialogueFindingDraft(Guid FindingId, DialogueFindingArea Area, DialogueSeverity Severity, string Rule, string Location, IReadOnlyList<int> ChapterNumbers, IReadOnlyList<string> SceneIds, IReadOnlyList<string> ExchangeIds, IReadOnlyList<string> SpeakerIds, IReadOnlyList<string> LineIds, IReadOnlyList<string> Spans, string Evidence, bool IsOpen = true);
public sealed record DialogueFinding(Guid FindingId, DialogueFindingArea Area, DialogueSeverity Severity, string Rule, string Location, IReadOnlyList<int> ChapterNumbers, IReadOnlyList<string> SceneIds, IReadOnlyList<string> ExchangeIds, IReadOnlyList<string> SpeakerIds, IReadOnlyList<string> LineIds, IReadOnlyList<string> Spans, string Evidence, bool IsOpen);

public sealed record DialogueReview(
    Guid ReviewId,
    Guid ProjectId,
    string WorkspaceId,
    Guid EditorialPlanId,
    Guid VoiceLineReviewId,
    long ExpectedVoiceLineRevision,
    string ExpectedVoiceLineDigest,
    int Version,
    string RuleSet,
    string Actor,
    string SnapshotJson,
    long Revision,
    DialogueReviewStatus Status,
    IReadOnlyList<DialogueFinding> Findings,
    DialogueDecision? Decision,
    string? DecisionReason,
    int? ExpectedRepairRevision,
    Guid? MessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record DialogueReviewCreateResult(DialogueReview Review, bool Replayed);

public enum DialogueReviewStatus { Proposed, Evaluated, Approved, Rejected, RepairRequired, Stale }
public enum DialogueDecision { Approve, Reject, ReturnToRepair }
public enum DialogueSeverity { Info, Minor, Major, Blocking }
public enum DialogueFindingArea { Subtext, Naturalness, TurnTaking, Attribution, VoiceDifferentiation, DramaticProgression, ExpositionLoad }

public sealed class DialogueEditingValidationException : Exception { public DialogueEditingValidationException(string message) : base(message) { } }
public sealed class DialogueEditingConflictException : Exception { public DialogueEditingConflictException(string message) : base(message) { } }
public sealed class DialogueEditingTransitionException : Exception { public DialogueEditingTransitionException(string message) : base(message) { } }

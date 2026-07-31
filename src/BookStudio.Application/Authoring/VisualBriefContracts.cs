namespace BookStudio.Application.Authoring;

public interface IVisualBriefStore
{
    ValueTask<VisualBriefCreateResult> CreateAsync(VisualBriefDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualBrief> ReviseAsync(VisualBriefReviseCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualBrief> ReviewAsync(VisualBriefReviewCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualBrief> DecideAsync(VisualBriefDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualBrief> MarkStaleAsync(VisualBriefStaleCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualBrief?> GetAsync(string workspaceId, Guid briefId, CancellationToken ct = default);
}

public sealed record VisualBriefDraft(
    Guid BriefId, Guid ProjectId, string WorkspaceId,
    Guid LegalRiskCaseId, long ExpectedLegalRiskRevision, string ExpectedLegalRiskDigest,
    Guid SubjectId, string SubjectReference, string SubjectDigest, int SubjectVersion,
    VisualBriefType BriefType, string TargetChannel, int Width, int Height,
    string CropMode, string SafeZoneJson, string ArtDirection, string Composition,
    string SubjectIdentity, string ContinuityConstraints, string Style, string Palette,
    string TypographyIntent, string AccessibilityIntent, IReadOnlyList<string> ProhibitedElements,
    IReadOnlyList<VisualContinuityReferenceDraft> ContinuityReferences,
    string Actor, string SnapshotJson, string RequestFingerprint);

public sealed record VisualBriefReviseCommand(
    Guid RequestId, string WorkspaceId, Guid BriefId, long ExpectedRevision,
    string ArtDirection, string Composition, string ContinuityConstraints, string Style,
    string Palette, string TypographyIntent, string AccessibilityIntent,
    IReadOnlyList<string> ProhibitedElements, string Reason, string Actor, string RequestFingerprint);

public sealed record VisualBriefReviewCommand(
    Guid RequestId, string WorkspaceId, Guid BriefId, long ExpectedRevision,
    Guid ReviewId, string ReviewerIdentity, string Scope, VisualBriefReviewDecision Decision,
    string Rationale, string Evidence, IReadOnlyList<string> BlockingFindings,
    string Actor, string RequestFingerprint);

public sealed record VisualBriefDecisionCommand(
    Guid RequestId, string WorkspaceId, Guid BriefId, long ExpectedRevision,
    VisualBriefDecision Decision, string Reason, string Actor, string RequestFingerprint);

public sealed record VisualBriefStaleCommand(
    Guid RequestId, string WorkspaceId, Guid BriefId, long ExpectedRevision,
    string Reason, string Actor, string RequestFingerprint);

public sealed record VisualContinuityReferenceDraft(Guid ReferenceId, VisualContinuityKind Kind, string AuthorityKey, string Digest, int Version, string Evidence);
public sealed record VisualContinuityReference(Guid ReferenceId, VisualContinuityKind Kind, string AuthorityKey, string Digest, int Version, string Evidence);
public sealed record VisualBriefReview(Guid ReviewId, string ReviewerIdentity, string Scope, VisualBriefReviewDecision Decision, string Rationale, string Evidence, IReadOnlyList<string> BlockingFindings, DateTimeOffset ReviewedAtUtc);

public sealed record VisualBrief(
    Guid BriefId, Guid ProjectId, string WorkspaceId,
    Guid LegalRiskCaseId, long ExpectedLegalRiskRevision, string ExpectedLegalRiskDigest,
    Guid SubjectId, string SubjectReference, string SubjectDigest, int SubjectVersion,
    VisualBriefType BriefType, string TargetChannel, int Width, int Height,
    string CropMode, string SafeZoneJson, string ArtDirection, string Composition,
    string SubjectIdentity, string ContinuityConstraints, string Style, string Palette,
    string TypographyIntent, string AccessibilityIntent, IReadOnlyList<string> ProhibitedElements,
    IReadOnlyList<VisualContinuityReference> ContinuityReferences,
    IReadOnlyList<VisualBriefReview> Reviews, long Revision, VisualBriefStatus Status,
    string? DecisionReason, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record VisualBriefCreateResult(VisualBrief Brief, bool Replayed);

public enum VisualBriefType { Cover, Chapter, Scene, Character, Location, Object, Diagram, MarketingAsset }
public enum VisualContinuityKind { Character, Location, Object, Motif, Series }
public enum VisualBriefStatus { Proposed, InReview, Approved, RepairRequired, Revoked, Stale }
public enum VisualBriefReviewDecision { Approve, Reject, RequireRepair }
public enum VisualBriefDecision { SubmitForReview, Approve, ReturnToRepair, Reopen, Revoke }

public sealed class VisualBriefValidationException : Exception { public VisualBriefValidationException(string message) : base(message) { } }
public sealed class VisualBriefConflictException : Exception { public VisualBriefConflictException(string message) : base(message) { } }
public sealed class VisualBriefTransitionException : Exception { public VisualBriefTransitionException(string message) : base(message) { } }

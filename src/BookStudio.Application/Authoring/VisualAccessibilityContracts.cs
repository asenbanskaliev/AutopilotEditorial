namespace BookStudio.Application.Authoring;

public interface IVisualAccessibilityStore
{
    ValueTask<VisualAccessibilitySubmissionResult> SubmitAsync(VisualAccessibilityCaseDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualAccessibilityState> RecordAssessmentAsync(VisualAccessibilityAssessmentCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualAccessibilityState> DecideAsync(VisualAccessibilityDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualAccessibilityState?> GetAsync(string workspaceId, Guid accessibilityCaseId, CancellationToken ct = default);
}

public interface IVisualAccessibilityAuthorityReader
{
    ValueTask<VisualAccessibilityAuthoritySnapshot> RequireCurrentAsync(VisualAccessibilityAuthorityReference authority, CancellationToken ct = default);
}

public sealed record VisualAccessibilityCaseDraft(
    Guid RequestId,
    Guid AccessibilityCaseId,
    Guid ProjectId,
    string WorkspaceId,
    VisualAccessibilityAuthorityReference Authority,
    VisualAccessibilityChannel Channel,
    string Locale,
    IReadOnlyList<VisualUseDraft> Visuals,
    string Actor,
    string RequestFingerprint);

public sealed record VisualAccessibilityAuthorityReference(
    Guid VisualBriefId,
    long VisualBriefRevision,
    string VisualBriefDigest,
    IReadOnlyList<VisualAccessibilityAssetAuthority> Assets,
    IReadOnlyList<VisualAccessibilityAuditAuthority> VisualAudits,
    VisualAccessibilityCoverAuthority? ApprovedCover);

public sealed record VisualAccessibilityAssetAuthority(Guid AssetId, long Revision, string ContentDigest);
public sealed record VisualAccessibilityAuditAuthority(Guid AuditId, long Revision, string Outcome, string EvidenceDigest);
public sealed record VisualAccessibilityCoverAuthority(Guid CoverProjectId, Guid VariantId, long Revision, string ArtifactDigest, string EvidenceDigest);

public sealed record VisualAccessibilityAuthoritySnapshot(
    VisualAccessibilityAuthorityReference Authority,
    bool IsCurrent,
    string AuthorityDigest,
    DateTimeOffset VerifiedAtUtc);

public sealed record VisualUseDraft(
    Guid VisualUseId,
    Guid AssetId,
    long AssetRevision,
    string AssetDigest,
    VisualUseKind Kind,
    VisualMeaning Meaning,
    int ReadingOrder,
    string? Caption,
    string? AssociatedTextReference,
    bool ContainsEssentialText,
    string? EmbeddedTextEquivalent);

public sealed record VisualAccessibilityAssessmentCommand(
    Guid RequestId,
    Guid AccessibilityCaseId,
    string WorkspaceId,
    long ExpectedRevision,
    IReadOnlyList<VisualAccessibilityAssessment> Assessments,
    string Actor,
    string RequestFingerprint);

public sealed record VisualAccessibilityAssessment(
    Guid AssessmentId,
    Guid VisualUseId,
    VisualAccessibilityAssessmentKind Kind,
    VisualAccessibilityOutcome Outcome,
    string? AltText,
    string? LongDescription,
    ContrastEvidence? Contrast,
    string Evidence,
    string EvidenceDigest,
    string? FindingCode,
    string? RepairRecommendation);

public sealed record ContrastEvidence(
    string Foreground,
    string Background,
    decimal MeasuredRatio,
    decimal RequiredRatio,
    bool IsLargeText,
    bool Passed,
    string EvidenceDigest);

public sealed record VisualAccessibilityDecisionCommand(
    Guid RequestId,
    Guid AccessibilityCaseId,
    string WorkspaceId,
    long ExpectedRevision,
    VisualAccessibilityDecision Decision,
    string Reason,
    string Evidence,
    string EvidenceDigest,
    string Actor,
    string RequestFingerprint);

public sealed record VisualAccessibilityFinding(
    Guid FindingId,
    Guid VisualUseId,
    string Code,
    VisualAccessibilitySeverity Severity,
    string Description,
    string Evidence,
    string EvidenceDigest,
    string? RepairRecommendation);

public sealed record VisualAccessibilityState(
    Guid AccessibilityCaseId,
    Guid ProjectId,
    string WorkspaceId,
    VisualAccessibilityAuthorityReference Authority,
    VisualAccessibilityChannel Channel,
    string Locale,
    IReadOnlyList<VisualUseDraft> Visuals,
    IReadOnlyList<VisualAccessibilityAssessment> Assessments,
    IReadOnlyList<VisualAccessibilityFinding> Findings,
    VisualAccessibilityStatus Status,
    long Revision,
    Guid? MessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record VisualAccessibilitySubmissionResult(VisualAccessibilityState AccessibilityCase, bool Replayed);

public enum VisualAccessibilityChannel { Print, Ebook, Web, Retailer, Social }
public enum VisualUseKind { Illustration, Photograph, Diagram, Chart, Cover, Logo, Decorative, TextInImage }
public enum VisualMeaning { Informative, Functional, Complex, Decorative }
public enum VisualAccessibilityAssessmentKind { AltText, DecorativeClassification, LongDescription, TextInImageEquivalent, Contrast, ReadingOrder, CaptionAssociation }
public enum VisualAccessibilityOutcome { Pass, Fail, ReviewRequired, NotApplicable }
public enum VisualAccessibilityDecision { Approve, ReturnToRepair, Reject, Supersede }
public enum VisualAccessibilityStatus { Draft, Assessed, ReviewRequired, Approved, RepairRequired, Rejected, Superseded }
public enum VisualAccessibilitySeverity { Advisory, Major, Blocking }

public sealed class VisualAccessibilityValidationException : Exception
{
    public VisualAccessibilityValidationException(string message) : base(message) { }
}

public sealed class VisualAccessibilityConflictException : Exception
{
    public VisualAccessibilityConflictException(string message) : base(message) { }
}

public sealed class VisualAccessibilityTransitionException : Exception
{
    public VisualAccessibilityTransitionException(string message) : base(message) { }
}

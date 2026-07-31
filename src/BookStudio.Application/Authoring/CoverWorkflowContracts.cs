namespace BookStudio.Application.Authoring;

public interface ICoverWorkflowStore
{
    ValueTask<CoverSubmissionResult> SubmitAsync(CoverProjectDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<CoverProjectState> AddVariantAsync(CoverVariantCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<CoverProjectState> DecideAsync(CoverDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<CoverProjectState?> GetAsync(string workspaceId, Guid coverProjectId, CancellationToken ct = default);
}

public interface ICoverAuthorityReader
{
    ValueTask<CoverAuthoritySnapshot> RequireCurrentAsync(CoverAuthorityReference authority, CancellationToken ct = default);
}

public sealed record CoverProjectDraft(
    Guid RequestId,
    Guid CoverProjectId,
    Guid ProjectId,
    string WorkspaceId,
    CoverAuthorityReference Authority,
    IReadOnlySet<CoverChannel> RequiredChannels,
    string Title,
    string? Subtitle,
    string Author,
    string? Series,
    string? Imprint,
    string? Blurb,
    string? Isbn,
    string Actor,
    string RequestFingerprint);

public sealed record CoverAuthorityReference(
    Guid VisualBriefId,
    long VisualBriefRevision,
    string VisualBriefDigest,
    IReadOnlyList<CoverAssetAuthority> Assets,
    IReadOnlyList<CoverAdapterLineage> AdapterLineage,
    IReadOnlyList<CoverAuditAuthority> VisualAudits);

public sealed record CoverAssetAuthority(Guid AssetId, long Revision, string ContentDigest);
public sealed record CoverAdapterLineage(Guid RequestId, Guid OutputId, string ProviderEvidenceDigest);
public sealed record CoverAuditAuthority(Guid AuditId, long Revision, string Outcome, string EvidenceDigest);

public sealed record CoverAuthoritySnapshot(
    CoverAuthorityReference Authority,
    bool IsCurrent,
    string AuthorityDigest,
    DateTimeOffset VerifiedAtUtc);

public sealed record CoverVariantCommand(
    Guid RequestId,
    Guid CoverProjectId,
    string WorkspaceId,
    long ExpectedRevision,
    CoverVariantDraft Variant,
    string Actor,
    string RequestFingerprint);

public sealed record CoverVariantDraft(
    Guid VariantId,
    CoverChannel Channel,
    CoverVariantKind Kind,
    Guid? SourceVariantId,
    CoverGeometry Geometry,
    CoverTypography Typography,
    IReadOnlyList<CoverPlacement> Placements,
    IReadOnlyList<CoverValidationEvidence> Validations,
    string ExportProfile,
    string ArtifactDigest);

public sealed record CoverGeometry(
    decimal Width,
    decimal Height,
    decimal Bleed,
    decimal SafeInset,
    decimal SpineWidth,
    CoverRect Front,
    CoverRect? Spine,
    CoverRect? Back,
    CoverRect? BarcodeZone);

public sealed record CoverRect(decimal X, decimal Y, decimal Width, decimal Height);

public sealed record CoverTypography(
    CoverTextBlock Title,
    CoverTextBlock? Subtitle,
    CoverTextBlock Author,
    CoverTextBlock? Series,
    CoverTextBlock? Imprint,
    CoverTextBlock? Blurb);

public sealed record CoverTextBlock(
    string Text,
    string FontFamily,
    decimal FontSize,
    decimal MinimumFontSize,
    decimal ContrastRatio,
    CoverRect Bounds,
    int HierarchyLevel);

public sealed record CoverPlacement(
    Guid PlacementId,
    Guid AssetId,
    long AssetRevision,
    string AssetDigest,
    CoverPlacementRole Role,
    CoverRect Bounds,
    string CropMode,
    string LineageEvidenceDigest);

public sealed record CoverValidationEvidence(
    Guid ValidationId,
    CoverValidationKind Kind,
    CoverValidationOutcome Outcome,
    string PolicyVersion,
    string Evidence,
    string EvidenceDigest);

public sealed record CoverDecisionCommand(
    Guid RequestId,
    Guid CoverProjectId,
    string WorkspaceId,
    long ExpectedRevision,
    Guid VariantId,
    CoverDecision Decision,
    string Reason,
    string Evidence,
    string EvidenceDigest,
    string Actor,
    string RequestFingerprint);

public sealed record CoverProjectState(
    Guid CoverProjectId,
    Guid ProjectId,
    string WorkspaceId,
    CoverAuthorityReference Authority,
    IReadOnlySet<CoverChannel> RequiredChannels,
    IReadOnlyList<CoverVariant> Variants,
    Guid? SelectedVariantId,
    CoverProjectStatus Status,
    long Revision,
    Guid? MessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CoverVariant(
    CoverVariantDraft Draft,
    CoverVariantStatus Status,
    string? DecisionReason,
    long Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CoverSubmissionResult(CoverProjectState Project, bool Replayed);

public enum CoverChannel { Print, Ebook, Thumbnail, Retailer, Social }
public enum CoverVariantKind { FullWrap, FrontOnly, Thumbnail, ChannelDerivative }
public enum CoverPlacementRole { Background, HeroImage, Logo, Barcode, Decorative }
public enum CoverValidationKind { Geometry, SafeZone, Bleed, Spine, Barcode, Typography, Contrast, ThumbnailLegibility, Crop, GenreFitness, ChannelFitness, Lineage }
public enum CoverValidationOutcome { Pass, Fail, ReviewRequired }
public enum CoverDecision { Select, Approve, ReturnToRepair, Reject, Supersede }
public enum CoverProjectStatus { Draft, CandidateReady, Selected, Approved, RepairRequired, Rejected, Superseded }
public enum CoverVariantStatus { Draft, Validated, Selected, Approved, RepairRequired, Rejected, Superseded }

public sealed class CoverWorkflowValidationException : Exception
{
    public CoverWorkflowValidationException(string message) : base(message) { }
}

public sealed class CoverWorkflowConflictException : Exception
{
    public CoverWorkflowConflictException(string message) : base(message) { }
}

public sealed class CoverWorkflowTransitionException : Exception
{
    public CoverWorkflowTransitionException(string message) : base(message) { }
}

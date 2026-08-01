namespace BookStudio.Application.Production;

public interface IPrintPdfRenderStore
{
    ValueTask<PrintPdfSubmissionResult> SubmitAsync(PrintPdfRenderRequest request, PrintPdfArtifact artifact, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<PrintPdfRenderState> ValidateAsync(PrintPdfValidationCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<PrintPdfRenderState> DecideAsync(PrintPdfDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<PrintPdfRenderState?> GetAsync(string workspaceId, Guid renderId, CancellationToken ct = default);
}

public interface IPrintPdfAuthorityReader
{
    ValueTask<PrintPdfAuthoritySnapshot> RequireCurrentApprovedAsync(PrintPdfAuthority authority, CancellationToken ct = default);
}

public sealed record PrintPdfRenderRequest(
    Guid RequestId,
    Guid RenderId,
    Guid ProjectId,
    string WorkspaceId,
    PrintPdfAuthority Authority,
    PrintGeometry Geometry,
    PrintBindingDirection BindingDirection,
    PrintPaperProfile Paper,
    string Locale,
    PrintPdfMetadata Metadata,
    IReadOnlyList<PrintFontResource> Fonts,
    IReadOnlyList<PrintImageResource> Images,
    string Actor,
    string RequestFingerprint);

public sealed record PrintPdfAuthority(
    Guid EpubRenderId,
    long Revision,
    string PackageDigest,
    string WorkspaceId,
    Guid ProjectId,
    string Status);

public sealed record PrintPdfAuthoritySnapshot(
    PrintPdfAuthority Authority,
    bool IsCurrent,
    bool IsApproved,
    IReadOnlyList<PrintSourcePage> SourcePages,
    DateTimeOffset VerifiedAtUtc);

public sealed record PrintGeometry(
    decimal TrimWidthPoints,
    decimal TrimHeightPoints,
    decimal BleedTopPoints,
    decimal BleedRightPoints,
    decimal BleedBottomPoints,
    decimal BleedLeftPoints,
    decimal MarginTopPoints,
    decimal MarginOutsidePoints,
    decimal MarginBottomPoints,
    decimal MarginInsidePoints);

public sealed record PrintPaperProfile(string ProfileId, string ColorSpace, string OutputIntentDigest);
public sealed record PrintPdfMetadata(string Identifier, string Title, IReadOnlyList<string> Authors, string Language, DateTimeOffset ModifiedAtUtc);
public sealed record PrintSourcePage(Guid SourceId, int Order, PrintPageKind Kind, string ContentDigest, string Content);
public sealed record PrintPageManifestEntry(Guid PageId, int PageNumber, PrintPageKind Kind, PrintPageSide Side, string ContentDigest, PrintPageBoxes Boxes);
public sealed record PrintPageBoxes(decimal MediaWidth, decimal MediaHeight, decimal TrimX, decimal TrimY, decimal TrimWidth, decimal TrimHeight, decimal BleedX, decimal BleedY, decimal BleedWidth, decimal BleedHeight);

public sealed record PrintFontResource(Guid FontId, string Family, string Style, string ContentDigest, bool EmbeddingPermitted, bool Embedded, IReadOnlyList<int> Glyphs);
public sealed record PrintImageResource(Guid ImageId, string ContentDigest, bool RightsApproved, int PixelWidth, int PixelHeight, decimal PlacedWidthPoints, decimal PlacedHeightPoints, string ColorProfile, string? AccessibilityAlternative);

public sealed record PrintPdfArtifact(
    string ArtifactDigest,
    IReadOnlyList<PrintPageManifestEntry> Pages,
    IReadOnlyList<Guid> EmbeddedFontIds,
    IReadOnlyList<Guid> EmbeddedImageIds,
    string MetadataDigest,
    string OutputIntentDigest);

public sealed record PrintPdfValidationCommand(Guid RequestId, Guid RenderId, string WorkspaceId, long ExpectedRevision, IReadOnlyList<PrintPdfFinding> ExternalFindings, string Actor, string RequestFingerprint);
public sealed record PrintPdfDecisionCommand(Guid RequestId, Guid RenderId, string WorkspaceId, long ExpectedRevision, PrintPdfDecision Decision, string Reason, string Evidence, string EvidenceDigest, string Actor, string RequestFingerprint);
public sealed record PrintPdfFinding(Guid FindingId, string Code, PrintPdfFindingCategory Category, PrintPdfSeverity Severity, string Description, Guid? ResourceId, int? PageNumber, string EvidenceDigest);

public sealed record PrintPdfRenderState(
    Guid RenderId,
    Guid ProjectId,
    string WorkspaceId,
    PrintPdfAuthority Authority,
    PrintGeometry Geometry,
    PrintPaperProfile Paper,
    PrintPdfMetadata Metadata,
    PrintPdfArtifact? Artifact,
    IReadOnlyList<PrintPdfFinding> Findings,
    PrintPdfRenderStatus Status,
    long Revision,
    Guid? MessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record PrintPdfSubmissionResult(PrintPdfRenderState Render, bool Replayed);

public enum PrintBindingDirection { LeftToRight, RightToLeft }
public enum PrintPageKind { FrontMatter, Body, BackMatter, IntentionalBlank }
public enum PrintPageSide { Recto, Verso }
public enum PrintPdfDecision { Approve, ReturnToRepair, Reject, Supersede }
public enum PrintPdfRenderStatus { Draft, Rendered, Validated, ReviewRequired, Approved, RepairRequired, Rejected, Superseded }
public enum PrintPdfFindingCategory { Geometry, Pagination, Typography, Image, Color, Link, Metadata, Accessibility, PdfPreflight }
public enum PrintPdfSeverity { Advisory, Major, Blocking }

public sealed class PrintPdfValidationException(string message) : Exception(message);
public sealed class PrintPdfConflictException(string message) : Exception(message);
public sealed class PrintPdfTransitionException(string message) : Exception(message);

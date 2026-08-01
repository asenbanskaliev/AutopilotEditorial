namespace BookStudio.Application.Production;

public interface IDocxRenderStore
{
    ValueTask<DocxSubmissionResult> SubmitAsync(DocxRenderRequest request, DocxArtifact artifact, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<DocxRenderState> ValidateAsync(DocxValidationCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<DocxRenderState> DecideAsync(DocxDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<DocxRenderState?> GetAsync(string workspaceId, Guid renderId, CancellationToken ct = default);
}

public interface IDocxAuthorityReader
{
    ValueTask<DocxAuthoritySnapshot> RequireCurrentApprovedAsync(DocxAuthority authority, CancellationToken ct = default);
}

public sealed record DocxRenderRequest(Guid RequestId, Guid RenderId, Guid ProjectId, string WorkspaceId, DocxAuthority Authority, string Locale, string TemplateProfile, string CompatibilityTarget, IReadOnlyList<DocxSection> Sections, IReadOnlyList<DocxResource> Resources, string Actor, string RequestFingerprint);
public sealed record DocxAuthority(Guid PrintRenderId, long Revision, string ArtifactDigest, string WorkspaceId, Guid ProjectId, string Status);
public sealed record DocxAuthoritySnapshot(DocxAuthority Authority, bool IsCurrent, bool IsApproved, DateTimeOffset VerifiedAtUtc);
public sealed record DocxSection(Guid SectionId, int Order, string Title, IReadOnlyList<DocxBlock> Blocks);
public sealed record DocxBlock(Guid BlockId, int Order, DocxBlockKind Kind, string Content, string StyleId, string ContentDigest, string? AccessibilityAlternative);
public sealed record DocxPart(string PartName, string ContentType, string ContentDigest, int Order);
public sealed record DocxRelationship(string RelationshipId, string SourcePart, string Target, string Type, bool External);
public sealed record DocxResource(Guid ResourceId, string PartName, string ContentDigest, bool RightsApproved, string? AccessibilityAlternative);
public sealed record DocxArtifact(string ArtifactDigest, string ManifestDigest, IReadOnlyList<DocxPart> Parts, IReadOnlyList<DocxRelationship> Relationships, IReadOnlyList<Guid> EmbeddedResourceIds, string MetadataDigest);
public sealed record DocxFinding(Guid FindingId, string Code, DocxFindingCategory Category, DocxSeverity Severity, string Description, string? PartName, Guid? ResourceId, string EvidenceDigest);
public sealed record DocxValidationCommand(Guid RequestId, Guid RenderId, string WorkspaceId, long ExpectedRevision, IReadOnlyList<DocxFinding> ExternalFindings, string Actor, string RequestFingerprint);
public sealed record DocxDecisionCommand(Guid RequestId, Guid RenderId, string WorkspaceId, long ExpectedRevision, DocxDecision Decision, string Reason, string Evidence, string EvidenceDigest, string Actor, string RequestFingerprint);
public sealed record DocxRenderState(Guid RenderId, Guid ProjectId, string WorkspaceId, DocxAuthority Authority, string Locale, string TemplateProfile, string CompatibilityTarget, DocxArtifact? Artifact, IReadOnlyList<DocxFinding> Findings, DocxRenderStatus Status, long Revision, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record DocxSubmissionResult(DocxRenderState Render, bool Replayed);

public enum DocxBlockKind { Heading, Paragraph, List, Table, Figure, Caption, Note, PageBreak, Header, Footer }
public enum DocxDecision { Approve, ReturnToRepair, Reject, Supersede }
public enum DocxRenderStatus { Draft, Rendered, Validated, ReviewRequired, Approved, RepairRequired, Rejected, Superseded }
public enum DocxFindingCategory { Package, Structure, Style, Relationship, Compatibility, Accessibility, Editability, Metadata }
public enum DocxSeverity { Advisory, Major, Blocking }
public sealed class DocxValidationException(string message) : Exception(message);
public sealed class DocxConflictException(string message) : Exception(message);
public sealed class DocxTransitionException(string message) : Exception(message);

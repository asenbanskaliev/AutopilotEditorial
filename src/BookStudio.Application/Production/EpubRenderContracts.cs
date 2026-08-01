namespace BookStudio.Application.Production;

public interface IEpubRenderStore
{
    ValueTask<EpubRenderSubmissionResult> SubmitAsync(EpubRenderRequest request, EpubPackage package, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<EpubRenderState> ValidateAsync(EpubValidationCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<EpubRenderState> DecideAsync(EpubDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<EpubRenderState?> GetAsync(string workspaceId, Guid renderId, CancellationToken ct = default);
}

public interface IEpubManuscriptAuthorityReader
{
    ValueTask<EpubManuscriptAuthoritySnapshot> RequireCurrentApprovedAsync(EpubManuscriptAuthority authority, CancellationToken ct = default);
}

public sealed record EpubRenderRequest(Guid RequestId, Guid RenderId, Guid ProjectId, string WorkspaceId,
    EpubManuscriptAuthority Manuscript, string Locale, EpubProfile Profile, EpubMetadata Metadata,
    IReadOnlyList<EpubResource> Resources, string Actor, string RequestFingerprint);

public sealed record EpubManuscriptAuthority(Guid AssemblyId, long Revision, string CanonicalContentDigest,
    string ManifestDigest, string WorkspaceId, Guid ProjectId, string Status);

public sealed record EpubManuscriptAuthoritySnapshot(EpubManuscriptAuthority Authority, bool IsCurrent,
    bool IsApproved, IReadOnlyList<EpubSection> Sections, DateTimeOffset VerifiedAtUtc);

public sealed record EpubSection(Guid SectionId, int Order, string Title, EpubSectionKind Kind,
    IReadOnlyList<EpubNode> Nodes);

public sealed record EpubNode(Guid NodeId, int Order, EpubNodeKind Kind, string Content, string ContentDigest,
    string? Caption, string? AccessibilityAlternative, string? CitationReference, string? NoteReference);

public sealed record EpubMetadata(string Identifier, string Title, IReadOnlyList<string> Authors,
    string Language, DateTimeOffset ModifiedAtUtc);

public sealed record EpubResource(Guid ResourceId, string Path, string MediaType, string ContentDigest,
    bool RightsApproved, byte[] Content);

public sealed record EpubPackage(string PackageDigest, IReadOnlyList<EpubPackageEntry> Entries,
    string NavigationPath, string PackageDocumentPath);

public sealed record EpubPackageEntry(string Path, string MediaType, string ContentDigest, long Length,
    EpubCompression Compression, int Order);

public sealed record EpubValidationCommand(Guid RequestId, Guid RenderId, string WorkspaceId,
    long ExpectedRevision, IReadOnlyList<EpubFinding> ExternalFindings, string Actor, string RequestFingerprint);

public sealed record EpubDecisionCommand(Guid RequestId, Guid RenderId, string WorkspaceId,
    long ExpectedRevision, EpubDecision Decision, string Reason, string Evidence, string EvidenceDigest,
    string Actor, string RequestFingerprint);

public sealed record EpubFinding(Guid FindingId, string Code, EpubFindingCategory Category,
    EpubSeverity Severity, string Description, string? EntryPath, string EvidenceDigest);

public sealed record EpubRenderState(Guid RenderId, Guid ProjectId, string WorkspaceId,
    EpubManuscriptAuthority Manuscript, EpubProfile Profile, EpubMetadata Metadata,
    EpubPackage? Package, IReadOnlyList<EpubFinding> Findings, EpubRenderStatus Status,
    long Revision, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record EpubRenderSubmissionResult(EpubRenderState Render, bool Replayed);

public enum EpubProfile { Epub3Reflowable, Epub3FixedLayout }
public enum EpubSectionKind { FrontMatter, Body, BackMatter }
public enum EpubNodeKind { Chapter, Scene, Paragraph, Citation, Footnote, Endnote, Table, Figure, Caption, AccessibilityAlternative }
public enum EpubCompression { Stored, Deflated }
public enum EpubDecision { Approve, ReturnToRepair, Reject, Supersede }
public enum EpubRenderStatus { Draft, Rendered, Validated, ReviewRequired, Approved, RepairRequired, Rejected, Superseded }
public enum EpubFindingCategory { Structure, Navigation, Metadata, Resource, Accessibility, EpubCheck }
public enum EpubSeverity { Advisory, Major, Blocking }

public sealed class EpubRenderValidationException(string message) : Exception(message);
public sealed class EpubRenderConflictException(string message) : Exception(message);
public sealed class EpubRenderTransitionException(string message) : Exception(message);

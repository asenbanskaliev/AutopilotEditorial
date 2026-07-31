namespace BookStudio.Application.Authoring;

public interface IManuscriptAssemblyStore
{
    ValueTask<ManuscriptAssemblySubmissionResult> SubmitAsync(ManuscriptAssemblyDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ManuscriptAssemblyState> ValidateAsync(ManuscriptAssemblyValidationCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ManuscriptAssemblyState> DecideAsync(ManuscriptAssemblyDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ManuscriptAssemblyState?> GetAsync(string workspaceId, Guid assemblyId, CancellationToken ct = default);
}

public interface IManuscriptAssemblyAuthorityReader
{
    ValueTask<ManuscriptAssemblyAuthoritySnapshot> RequireCurrentAsync(ManuscriptAssemblyAuthority authority, CancellationToken ct = default);
}

public sealed record ManuscriptAssemblyDraft(
    Guid RequestId,
    Guid AssemblyId,
    Guid ProjectId,
    string WorkspaceId,
    string Locale,
    IReadOnlyList<ManuscriptTargetChannel> TargetChannels,
    ManuscriptAssemblyAuthority Authority,
    IReadOnlyList<ManuscriptSectionDraft> Sections,
    IReadOnlyList<ManuscriptSourceBinding> ExcludedOptionalSources,
    string Actor,
    string RequestFingerprint);

public sealed record ManuscriptAssemblyAuthority(
    IReadOnlyList<ManuscriptSourceBinding> EditorialSources,
    IReadOnlyList<ManuscriptSourceBinding> ResearchSources,
    IReadOnlyList<ManuscriptSourceBinding> RightsSources,
    IReadOnlyList<ManuscriptSourceBinding> VisualSources,
    IReadOnlyList<ManuscriptSourceBinding> AccessibilitySources,
    ManuscriptSourceBinding? CoverSource);

public sealed record ManuscriptSourceBinding(
    string SliceId,
    Guid SourceId,
    long Revision,
    string ContentDigest,
    string EvidenceDigest,
    ManuscriptSourceStatus Status,
    string WorkspaceId,
    Guid ProjectId);

public sealed record ManuscriptAssemblyAuthoritySnapshot(
    ManuscriptAssemblyAuthority Authority,
    bool IsCurrent,
    string AuthorityDigest,
    DateTimeOffset VerifiedAtUtc);

public sealed record ManuscriptSectionDraft(
    Guid SectionId,
    ManuscriptSectionKind Kind,
    int Order,
    string Title,
    IReadOnlyList<ManuscriptContentNode> Nodes);

public sealed record ManuscriptContentNode(
    Guid NodeId,
    ManuscriptContentKind Kind,
    int Order,
    string Content,
    IReadOnlyList<Guid> SourceIds,
    string ContentDigest,
    string? Caption,
    string? AccessibilityAlternative,
    string? CitationReference,
    string? RightsReference);

public sealed record ManuscriptAssemblyValidationCommand(
    Guid RequestId,
    Guid AssemblyId,
    string WorkspaceId,
    long ExpectedRevision,
    string Actor,
    string RequestFingerprint);

public sealed record ManuscriptAssemblyDecisionCommand(
    Guid RequestId,
    Guid AssemblyId,
    string WorkspaceId,
    long ExpectedRevision,
    ManuscriptAssemblyDecision Decision,
    string Reason,
    string Evidence,
    string EvidenceDigest,
    string Actor,
    string RequestFingerprint);

public sealed record ManuscriptAssemblyFinding(
    Guid FindingId,
    string Code,
    ManuscriptAssemblySeverity Severity,
    string Description,
    Guid? SectionId,
    Guid? NodeId,
    string EvidenceDigest);

public sealed record ManuscriptCanonicalManifest(
    string CanonicalContentDigest,
    string ManifestDigest,
    IReadOnlyList<Guid> OrderedSectionIds,
    IReadOnlyList<Guid> OrderedNodeIds,
    IReadOnlyList<ManuscriptSourceBinding> IncludedSources,
    IReadOnlyList<ManuscriptSourceBinding> ExcludedOptionalSources);

public sealed record ManuscriptAssemblyState(
    Guid AssemblyId,
    Guid ProjectId,
    string WorkspaceId,
    string Locale,
    IReadOnlyList<ManuscriptTargetChannel> TargetChannels,
    ManuscriptAssemblyAuthority Authority,
    IReadOnlyList<ManuscriptSectionDraft> Sections,
    IReadOnlyList<ManuscriptAssemblyFinding> Findings,
    ManuscriptCanonicalManifest? Manifest,
    ManuscriptAssemblyStatus Status,
    long Revision,
    Guid? MessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ManuscriptAssemblySubmissionResult(ManuscriptAssemblyState Assembly, bool Replayed);

public enum ManuscriptTargetChannel { Print, Ebook, Web }
public enum ManuscriptSectionKind { FrontMatter, Body, BackMatter }
public enum ManuscriptContentKind { Chapter, Scene, Paragraph, Citation, Footnote, Endnote, Table, Figure, Caption, AccessibilityAlternative }
public enum ManuscriptSourceStatus { Approved, Pass, Rejected, Superseded }
public enum ManuscriptAssemblyDecision { Approve, ReturnToRepair, Reject, Supersede }
public enum ManuscriptAssemblyStatus { Draft, Validated, ReviewRequired, Approved, RepairRequired, Rejected, Superseded }
public enum ManuscriptAssemblySeverity { Advisory, Major, Blocking }

public sealed class ManuscriptAssemblyValidationException : Exception
{
    public ManuscriptAssemblyValidationException(string message) : base(message) { }
}

public sealed class ManuscriptAssemblyConflictException : Exception
{
    public ManuscriptAssemblyConflictException(string message) : base(message) { }
}

public sealed class ManuscriptAssemblyTransitionException : Exception
{
    public ManuscriptAssemblyTransitionException(string message) : base(message) { }
}

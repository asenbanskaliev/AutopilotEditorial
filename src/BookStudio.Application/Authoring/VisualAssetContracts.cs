namespace BookStudio.Application.Authoring;

public interface IVisualAssetStore
{
    ValueTask<VisualAssetCreateResult> RegisterAsync(VisualAssetDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualAsset> AddEvidenceAsync(VisualAssetEvidenceCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualAsset> ValidateAsync(VisualAssetValidationCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualAsset> TransitionAsync(VisualAssetTransitionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualAsset?> GetAsync(string workspaceId, Guid assetId, CancellationToken ct = default);
}

public sealed record VisualAssetDraft(
    Guid AssetId,
    Guid ProjectId,
    string WorkspaceId,
    Guid VisualBriefId,
    long ExpectedVisualBriefRevision,
    string ExpectedVisualBriefDigest,
    VisualAssetType AssetType,
    string SourceAdapter,
    string CanonicalStoragePath,
    string MediaFormat,
    int Width,
    int Height,
    string ColorProfile,
    string ContentDigest,
    string GenerationParametersJson,
    string CausalSnapshotJson,
    VisualAssetProvenance Provenance,
    VisualAssetRights Rights,
    VisualAssetAccessibility Accessibility,
    IReadOnlyList<VisualAssetRelationshipDraft> Relationships,
    string Actor,
    string RequestFingerprint);

public sealed record VisualAssetEvidenceCommand(
    Guid RequestId,
    string WorkspaceId,
    Guid AssetId,
    long ExpectedRevision,
    VisualAssetProvenance Provenance,
    VisualAssetRights Rights,
    VisualAssetAccessibility Accessibility,
    string Actor,
    string RequestFingerprint);

public sealed record VisualAssetValidationCommand(
    Guid RequestId,
    string WorkspaceId,
    Guid AssetId,
    long ExpectedRevision,
    string Validator,
    string PolicyVersion,
    bool DigestMatches,
    bool StorageIntegrityValid,
    bool FormatValid,
    bool DimensionsValid,
    bool ColorProfileValid,
    IReadOnlyList<string> Findings,
    string Evidence,
    string Actor,
    string RequestFingerprint);

public sealed record VisualAssetTransitionCommand(
    Guid RequestId,
    string WorkspaceId,
    Guid AssetId,
    long ExpectedRevision,
    VisualAssetTransition Transition,
    string Reason,
    Guid? SupersedingAssetId,
    string Actor,
    string RequestFingerprint);

public sealed record VisualAssetProvenance(
    string Provider,
    string Model,
    string PromptDigest,
    string InputLineageJson,
    string SourceEvidence);

public sealed record VisualAssetRights(
    string License,
    string RightsHolder,
    string Territory,
    DateTimeOffset? ValidUntilUtc,
    string Evidence);

public sealed record VisualAssetAccessibility(
    string AltText,
    string LongDescription,
    string ReadingOrderHint,
    string Evidence);

public sealed record VisualAssetRelationshipDraft(
    Guid RelationshipId,
    VisualAssetRelationshipKind Kind,
    Guid RelatedAssetId,
    string Evidence);

public sealed record VisualAssetRelationship(
    Guid RelationshipId,
    VisualAssetRelationshipKind Kind,
    Guid RelatedAssetId,
    string Evidence);

public sealed record VisualAssetTechnicalValidation(
    Guid ValidationId,
    string Validator,
    string PolicyVersion,
    bool DigestMatches,
    bool StorageIntegrityValid,
    bool FormatValid,
    bool DimensionsValid,
    bool ColorProfileValid,
    IReadOnlyList<string> Findings,
    string Evidence,
    DateTimeOffset ValidatedAtUtc);

public sealed record VisualAsset(
    Guid AssetId,
    Guid ProjectId,
    string WorkspaceId,
    Guid VisualBriefId,
    long ExpectedVisualBriefRevision,
    string ExpectedVisualBriefDigest,
    VisualAssetType AssetType,
    string SourceAdapter,
    string CanonicalStoragePath,
    string MediaFormat,
    int Width,
    int Height,
    string ColorProfile,
    string ContentDigest,
    string GenerationParametersJson,
    string CausalSnapshotJson,
    VisualAssetProvenance Provenance,
    VisualAssetRights Rights,
    VisualAssetAccessibility Accessibility,
    IReadOnlyList<VisualAssetRelationship> Relationships,
    IReadOnlyList<VisualAssetTechnicalValidation> TechnicalValidations,
    long Revision,
    VisualAssetStatus Status,
    string? DecisionReason,
    Guid? SupersedingAssetId,
    Guid? MessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record VisualAssetCreateResult(VisualAsset Asset, bool Replayed);

public enum VisualAssetType
{
    Cover,
    ChapterIllustration,
    SceneImage,
    CharacterReference,
    LocationReference,
    ObjectReference,
    Diagram,
    MarketingAsset,
    DerivedRendition
}

public enum VisualAssetStatus { Registered, Validated, Approved, Quarantined, RepairRequired, Superseded, Revoked, Stale }
public enum VisualAssetTransition { Approve, Quarantine, RequireRepair, Repair, Supersede, Revoke, MarkStale }
public enum VisualAssetRelationshipKind { Parent, DerivedFrom, Supersedes, RenditionOf }

public sealed class VisualAssetValidationException : Exception
{
    public VisualAssetValidationException(string message) : base(message) { }
}

public sealed class VisualAssetConflictException : Exception
{
    public VisualAssetConflictException(string message) : base(message) { }
}

public sealed class VisualAssetTransitionException : Exception
{
    public VisualAssetTransitionException(string message) : base(message) { }
}

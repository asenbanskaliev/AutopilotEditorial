namespace BookStudio.Application.Authoring;

public interface IAssetRegistryStore
{
    ValueTask<AssetRegistrationResult> RegisterAsync(AssetRegistrationDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualAsset> ValidateAsync(AssetValidationCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualAsset> DecideAsync(AssetDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualAsset> QuarantineAsync(AssetQuarantineCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualAsset> RepairAsync(AssetRepairCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualAsset> SupersedeAsync(AssetSupersedeCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualAsset> MarkStaleAsync(AssetStaleCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<VisualAsset?> GetAsync(string workspaceId, Guid assetId, CancellationToken ct = default);
}

public sealed record AssetRegistrationDraft(
    Guid AssetId, Guid ProjectId, string WorkspaceId,
    Guid VisualBriefId, long ExpectedVisualBriefRevision, string ExpectedVisualBriefDigest,
    VisualAssetType AssetType, string SourceAdapter, string StorageRoot, string RelativePath,
    string MediaFormat, int Width, int Height, string ColorProfile, string ContentDigest,
    string CausalSnapshotJson, string GenerationParametersJson,
    AssetProvenanceEvidence Provenance, AssetRightsEvidence Rights,
    AssetAccessibilityEvidence Accessibility, IReadOnlyList<AssetRelationshipDraft> Relationships,
    string Actor, string RequestFingerprint);

public sealed record AssetValidationCommand(
    Guid RequestId, string WorkspaceId, Guid AssetId, long ExpectedRevision,
    string ValidatorIdentity, string PolicyVersion, string ArtifactDigest,
    IReadOnlyList<AssetTechnicalValidation> Validations,
    string Actor, string RequestFingerprint);

public sealed record AssetDecisionCommand(
    Guid RequestId, string WorkspaceId, Guid AssetId, long ExpectedRevision,
    AssetDecision Decision, string Reason, string Actor, string RequestFingerprint);

public sealed record AssetQuarantineCommand(
    Guid RequestId, string WorkspaceId, Guid AssetId, long ExpectedRevision,
    string Reason, string Evidence, string Actor, string RequestFingerprint);

public sealed record AssetRepairCommand(
    Guid RequestId, string WorkspaceId, Guid AssetId, long ExpectedRevision,
    string StorageRoot, string RelativePath, string MediaFormat, int Width, int Height,
    string ColorProfile, string ContentDigest, AssetProvenanceEvidence Provenance,
    AssetRightsEvidence Rights, AssetAccessibilityEvidence Accessibility,
    IReadOnlyList<AssetTechnicalValidation> Validations,
    string Reason, string Actor, string RequestFingerprint);

public sealed record AssetSupersedeCommand(
    Guid RequestId, string WorkspaceId, Guid AssetId, long ExpectedRevision,
    Guid SuccessorAssetId, string Reason, string Actor, string RequestFingerprint);

public sealed record AssetStaleCommand(
    Guid RequestId, string WorkspaceId, Guid AssetId, long ExpectedRevision,
    AssetDriftKind DriftKind, string Reason, string Actor, string RequestFingerprint);

public sealed record AssetProvenanceEvidence(
    string Provider, string Model, string SourceUri, string PromptDigest,
    string InputLineageJson, string EvidenceDigest, DateTimeOffset CapturedAtUtc);

public sealed record AssetRightsEvidence(
    string LicenseKind, string LicenseReference, string RightsHolder,
    string Territory, DateTimeOffset? ValidFromUtc, DateTimeOffset? ValidUntilUtc,
    string EvidenceDigest);

public sealed record AssetAccessibilityEvidence(
    string AltText, string LongDescription, string Language, string EvidenceDigest);

public sealed record AssetTechnicalValidation(
    Guid ValidationId, AssetValidationKind Kind, AssetValidationOutcome Outcome,
    string PolicyVersion, string Evidence, string EvidenceDigest);

public sealed record AssetRelationshipDraft(Guid RelationshipId, AssetRelationshipKind Kind, Guid RelatedAssetId, string Evidence);
public sealed record AssetRelationship(Guid RelationshipId, AssetRelationshipKind Kind, Guid RelatedAssetId, string Evidence);

public sealed record VisualAsset(
    Guid AssetId, Guid ProjectId, string WorkspaceId,
    Guid VisualBriefId, long ExpectedVisualBriefRevision, string ExpectedVisualBriefDigest,
    VisualAssetType AssetType, string SourceAdapter, string StorageRoot, string RelativePath,
    string MediaFormat, int Width, int Height, string ColorProfile, string ContentDigest,
    string CausalSnapshotJson, string GenerationParametersJson,
    AssetProvenanceEvidence Provenance, AssetRightsEvidence Rights,
    AssetAccessibilityEvidence Accessibility, IReadOnlyList<AssetTechnicalValidation> Validations,
    IReadOnlyList<AssetRelationship> Relationships, Guid? SupersededByAssetId,
    long Revision, VisualAssetStatus Status, string? DecisionReason, Guid? MessageId,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record AssetRegistrationResult(VisualAsset Asset, bool Replayed);

public enum VisualAssetType { Cover, ChapterIllustration, SceneImage, CharacterReference, LocationReference, ObjectReference, Diagram, MarketingAsset, DerivedRendition }
public enum VisualAssetStatus { Registered, Validated, Approved, RepairRequired, Quarantined, Superseded, Revoked, Stale }
public enum AssetDecision { Approve, ReturnToRepair, Reopen, Revoke }
public enum AssetValidationKind { DigestIntegrity, SafePath, MediaFormat, Dimensions, ColorProfile, ArtifactStoreIntegrity, Provenance, Rights, Accessibility, BriefAuthority }
public enum AssetValidationOutcome { Pass, Fail }
public enum AssetRelationshipKind { Parent, DerivedFrom, Supersedes, RenditionOf, References }
public enum AssetDriftKind { Brief, Content, Rights, Storage, Digest, TechnicalPolicy, Provenance }

public sealed class AssetRegistryValidationException : Exception { public AssetRegistryValidationException(string message) : base(message) { } }
public sealed class AssetRegistryConflictException : Exception { public AssetRegistryConflictException(string message) : base(message) { } }
public sealed class AssetRegistryTransitionException : Exception { public AssetRegistryTransitionException(string message) : base(message) { } }

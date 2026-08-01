namespace BookStudio.Application.Publishing;

public interface IKdpPackageStore
{
    ValueTask<KdpPackageSubmissionResult> SubmitAsync(KdpPackageRequest request, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<KdpPackageState> EvaluateAsync(KdpPackageEvaluationCommand command, KdpPackageManifest manifest,
        IReadOnlyList<KdpMetadataFinding> findings, string evidenceDigest, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<KdpPackageState> DecideAsync(KdpPackageDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<KdpPackageState?> GetAsync(string workspaceId, Guid packageId, CancellationToken ct = default);
}

public interface IKdpPackageAuthorityReader
{
    ValueTask<KdpPackageAuthoritySnapshot> RequireCurrentAsync(KdpPackageAuthority authority, CancellationToken ct = default);
}

public interface IKdpArtifactReader
{
    ValueTask<KdpArtifactSnapshot> ReadAsync(KdpPackageArtifact artifact, CancellationToken ct = default);
}

public sealed record KdpPackageRequest(Guid RequestId, Guid PackageId, Guid ProjectId, string WorkspaceId,
    KdpPackageAuthority Authority, KdpMetadata Metadata, IReadOnlyList<KdpPackageArtifact> Artifacts,
    string Marketplace, string Language, string FormatProfile, string ProfileVersion, string Actor,
    string RequestFingerprint);

public sealed record KdpPackageAuthority(Guid TechnicalPreflightRunId, long TechnicalPreflightRevision,
    string TechnicalPreflightEvidenceDigest, string WorkspaceId, Guid ProjectId, KdpPackageAuthorityStatus Status);

public sealed record KdpPackageAuthoritySnapshot(KdpPackageAuthority Authority, bool IsCurrent,
    string AuthorityDigest, DateTimeOffset VerifiedAtUtc);

public sealed record KdpMetadata(string Title, string? Subtitle, IReadOnlyList<KdpContributor> Contributors,
    string Description, IReadOnlyList<string> Keywords, IReadOnlyList<string> Categories, string Language,
    IReadOnlyList<string> Territories, KdpPublicationRights PublicationRights, string PricingIntent,
    string? Identifier, KdpAiDisclosure AiDisclosure);

public sealed record KdpContributor(string Name, string Role);
public sealed record KdpAiDisclosure(bool ContainsAiGeneratedText, bool ContainsAiGeneratedImages,
    bool ContainsAiAssistedText, bool ContainsAiAssistedImages, string Evidence);

public sealed record KdpPackageArtifact(string Path, string MediaType, long ByteLength, string Sha256Digest,
    KdpArtifactKind Kind);

public sealed record KdpArtifactSnapshot(KdpPackageArtifact Artifact, ReadOnlyMemory<byte> Content,
    string VerifiedSha256Digest);

public sealed record KdpManifestEntry(string Path, string MediaType, long ByteLength, string Sha256Digest,
    KdpArtifactKind Kind);

public sealed record KdpPackageManifest(IReadOnlyList<KdpManifestEntry> Entries, string CanonicalJson,
    string ManifestDigest, string PackageDigest);

public sealed record KdpMetadataFinding(Guid FindingId, string Code, KdpFindingSeverity Severity,
    string Field, string RuleId, string Description, string EvidenceDigest, KdpFindingStatus Status);

public sealed record KdpPackageEvaluationCommand(Guid RequestId, Guid PackageId, string WorkspaceId,
    long ExpectedRevision, string Actor, string RequestFingerprint);

public sealed record KdpPackageDecisionCommand(Guid RequestId, Guid PackageId, string WorkspaceId,
    long ExpectedRevision, KdpPackageDecision Decision, string Reason, string Evidence, string EvidenceDigest,
    string Actor, string RequestFingerprint);

public sealed record KdpPackageState(Guid PackageId, Guid ProjectId, string WorkspaceId,
    KdpPackageAuthority Authority, KdpMetadata Metadata, IReadOnlyList<KdpPackageArtifact> Artifacts,
    string Marketplace, string Language, string FormatProfile, string ProfileVersion,
    KdpPackageManifest? Manifest, IReadOnlyList<KdpMetadataFinding> Findings, string? EvidenceDigest,
    KdpPackageStatus Status, long Revision, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record KdpPackageSubmissionResult(KdpPackageState State, bool Replayed);

public enum KdpPackageAuthorityStatus { Approved, Rejected, Superseded }
public enum KdpPublicationRights { Owned, PublicDomain, Licensed }
public enum KdpArtifactKind { Manuscript, Cover, Supplemental }
public enum KdpFindingSeverity { Advisory, Major, Blocking }
public enum KdpFindingStatus { Open, Resolved }
public enum KdpPackageDecision { Approve, ReturnToRepair, Reject, Supersede }
public enum KdpPackageStatus { Draft, Evaluated, Approved, RepairRequired, Rejected, Superseded }

public sealed class KdpPackageValidationException : Exception { public KdpPackageValidationException(string message) : base(message) { } }
public sealed class KdpPackageConflictException : Exception { public KdpPackageConflictException(string message) : base(message) { } }
public sealed class KdpPackageTransitionException : Exception { public KdpPackageTransitionException(string message) : base(message) { } }

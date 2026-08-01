namespace BookStudio.Application.Publishing;

public interface IProfessionalReleaseStore
{
    ValueTask<ProfessionalReleaseSubmissionResult> SubmitAsync(ProfessionalReleaseRequest request, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ProfessionalReleaseState> FreezeAsync(ProfessionalReleaseFreezeCommand command,
        IReadOnlyList<VerifiedReleaseArtifact> artifacts, ProfessionalReleaseManifest manifest,
        string inventoryDigest, string evidenceDigest, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ProfessionalReleaseState> DecideAsync(ProfessionalReleaseDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ProfessionalReleaseState?> GetAsync(string workspaceId, Guid releaseId, CancellationToken ct = default);
}

public interface IProofReleaseAuthorityReader
{
    ValueTask<ProofReleaseAuthoritySnapshot> RequireCurrentAsync(ProofReleaseAuthority authority, CancellationToken ct = default);
}

public interface IReleaseArtifactReader
{
    ValueTask<ReleaseArtifactSnapshot> ReadAsync(ReleaseArtifactReference artifact, CancellationToken ct = default);
}

public sealed record ProfessionalReleaseRequest(Guid RequestId, Guid ReleaseId, Guid ProjectId, string WorkspaceId,
    ProofReleaseAuthority Authority, string Channel, string SemanticVersion, string Locale, string Actor,
    string RequestFingerprint, IReadOnlyList<ReleaseArtifactReference> Artifacts, Guid? SupersedesReleaseId = null);

public sealed record ProofReleaseAuthority(Guid ProofId, long ProofRevision, string ProofEvidenceDigest,
    Guid PackageId, string PackageDigest, string WorkspaceId, Guid ProjectId, ProofReleaseAuthorityStatus Status);

public sealed record ProofReleaseAuthoritySnapshot(ProofReleaseAuthority Authority, bool IsCurrent,
    string AuthorityDigest, DateTimeOffset VerifiedAtUtc);

public sealed record ReleaseArtifactReference(string LogicalName, string MediaType, long ByteLength, string Digest,
    string Provenance, string SourceAuthority, bool Required);

public sealed record ReleaseArtifactSnapshot(string LogicalName, string MediaType, long ByteLength, string Digest,
    string Provenance, string SourceAuthority, ReadOnlyMemory<byte> Content);

public sealed record VerifiedReleaseArtifact(string LogicalName, string MediaType, long ByteLength, string Digest,
    string Provenance, string SourceAuthority, bool Required);

public sealed record ProfessionalReleaseManifest(Guid ReleaseId, string Channel, string SemanticVersion, string Locale,
    IReadOnlyList<VerifiedReleaseArtifact> Artifacts, string ManifestDigest, DateTimeOffset FrozenAtUtc);

public sealed record ProfessionalReleaseFreezeCommand(Guid RequestId, Guid ReleaseId, string WorkspaceId,
    long ExpectedRevision, string Actor, string RequestFingerprint);

public sealed record ProfessionalReleaseDecisionCommand(Guid RequestId, Guid ReleaseId, string WorkspaceId,
    long ExpectedRevision, ProfessionalReleaseDecision Decision, string Reason, string Evidence,
    string EvidenceDigest, string Actor, string RequestFingerprint);

public sealed record ProfessionalReleaseState(Guid ReleaseId, Guid ProjectId, string WorkspaceId,
    ProofReleaseAuthority Authority, string Channel, string SemanticVersion, string Locale,
    Guid? SupersedesReleaseId, IReadOnlyList<VerifiedReleaseArtifact> Artifacts,
    ProfessionalReleaseManifest? Manifest, string? InventoryDigest, string? EvidenceDigest,
    ProfessionalReleaseStatus Status, long Revision, Guid? MessageId,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record ProfessionalReleaseSubmissionResult(ProfessionalReleaseState State, bool Replayed);

public enum ProofReleaseAuthorityStatus { Approved, Rejected, Superseded }
public enum ProfessionalReleaseDecision { Approve, Reject, Supersede }
public enum ProfessionalReleaseStatus { Draft, Frozen, Approved, Rejected, Superseded }

public sealed class ProfessionalReleaseValidationException : Exception
{
    public ProfessionalReleaseValidationException(string message) : base(message) { }
}

public sealed class ProfessionalReleaseConflictException : Exception
{
    public ProfessionalReleaseConflictException(string message) : base(message) { }
}

public sealed class ProfessionalReleaseTransitionException : Exception
{
    public ProfessionalReleaseTransitionException(string message) : base(message) { }
}

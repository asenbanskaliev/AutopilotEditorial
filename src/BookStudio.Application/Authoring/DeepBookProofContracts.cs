namespace BookStudio.Application.Authoring;

public interface IDeepBookProofStore
{
    ValueTask<DeepBookProofCheckpoint?> LoadAsync(string workspaceId, Guid proofId, CancellationToken ct = default);
    ValueTask SaveAsync(DeepBookProofCheckpoint checkpoint, long expectedRevision, CancellationToken ct = default);
}

public sealed record DeepBookProofPolicy(
    decimal MaximumCost,
    string CostCurrency,
    int MaximumRepairAttempts,
    IReadOnlySet<string> RequiredFormats,
    bool RequireImageEvidence = false);

public sealed record DeepBookProofRequest(
    Guid ProofId,
    Guid JourneyId,
    string WorkspaceId,
    string NaturalLanguageIdea,
    DeepBookProofPolicy Policy,
    string Actor);

public sealed record DeepBookArtifact(
    string Format,
    string RelativePath,
    string MediaType,
    long ByteSize,
    string Sha256,
    string Provenance,
    bool Verified);

public sealed record DeepBookProofCheckpoint(
    Guid ProofId,
    Guid JourneyId,
    string WorkspaceId,
    DeepBookProofStatus Status,
    DeepBookProofPhase Phase,
    long Revision,
    decimal AccumulatedCost,
    int RepairAttempts,
    IReadOnlyList<DeepBookArtifact> Artifacts,
    HashSet<DeepBookProofPhase> CompletedPhases,
    string EvidenceDigest,
    string? BlockingReason,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<ImageArtifactEvidence>? ImageArtifacts = null);

public sealed record DeepBookProofStepResult(
    DeepBookProofCheckpoint Checkpoint,
    bool Replayed,
    bool ReadyForPublication);

public enum DeepBookProofStatus { Active, WaitingForDecision, Ready, Cancelled, Failed }
public enum DeepBookProofPhase { Intake, JourneyExecution, ArtifactProduction, ArtifactVerification, PublicationReady }

public sealed class DeepBookProofValidationException : Exception
{
    public DeepBookProofValidationException(string message) : base(message) { }
}

public sealed class DeepBookProofConflictException : Exception
{
    public DeepBookProofConflictException(string message) : base(message) { }
}

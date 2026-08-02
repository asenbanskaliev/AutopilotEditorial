using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Application.Authoring;

public sealed class DeepBookProofCoordinator
{
    private static readonly string[] CanonicalFormats = ["EPUB", "PDF", "DOCX", "KDP"];
    private readonly IDeepBookProofStore _store;
    private readonly string _artifactRoot;

    public DeepBookProofCoordinator(IDeepBookProofStore store, string artifactRoot)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _artifactRoot = Path.GetFullPath(artifactRoot ?? throw new ArgumentNullException(nameof(artifactRoot)));
        Directory.CreateDirectory(_artifactRoot);
    }

    public async ValueTask<DeepBookProofStepResult> StartOrResumeAsync(
        DeepBookProofRequest request,
        BookCreationJourney journey,
        decimal stepCost,
        IReadOnlyList<DeepBookArtifact>? producedArtifacts,
        DateTimeOffset at,
        CancellationToken ct = default,
        IReadOnlyList<ImageArtifactEvidence>? producedImages = null)
    {
        ValidateRequest(request, journey, stepCost);
        var current = await _store.LoadAsync(request.WorkspaceId, request.ProofId, ct);
        if (current is null)
        {
            current = NewCheckpoint(request, at);
            await _store.SaveAsync(current, 0, ct);
            return new DeepBookProofStepResult(current, false, false);
        }

        if (current.Status is DeepBookProofStatus.Ready or DeepBookProofStatus.Cancelled or DeepBookProofStatus.Failed)
            return new DeepBookProofStepResult(current, true, current.Status == DeepBookProofStatus.Ready);

        var nextCost = current.AccumulatedCost + stepCost;
        if (nextCost > request.Policy.MaximumCost)
        {
            var blocked = Advance(current, DeepBookProofStatus.WaitingForDecision, current.Phase, nextCost,
                current.RepairAttempts, current.Artifacts, current.CompletedPhases, "Cost policy exceeded.", at, current.ImageArtifacts);
            await _store.SaveAsync(blocked, current.Revision, ct);
            return new DeepBookProofStepResult(blocked, false, false);
        }

        var next = current.Phase switch
        {
            DeepBookProofPhase.Intake => CompletePhase(current, DeepBookProofPhase.Intake, DeepBookProofPhase.JourneyExecution, nextCost, at),
            DeepBookProofPhase.JourneyExecution when journey.Status == JourneyStatus.Completed =>
                CompletePhase(current, DeepBookProofPhase.JourneyExecution, DeepBookProofPhase.ArtifactProduction, nextCost, at),
            DeepBookProofPhase.JourneyExecution => current with { AccumulatedCost = nextCost },
            DeepBookProofPhase.ArtifactProduction when producedArtifacts is { Count: > 0 } =>
                AddArtifacts(current, producedArtifacts, producedImages, nextCost, at),
            DeepBookProofPhase.ArtifactVerification => VerifyAndFinalize(current, request.Policy, nextCost, at),
            _ => current
        };

        if (ReferenceEquals(next, current) || next == current)
            return new DeepBookProofStepResult(current, true, false);

        next = next with { Revision = current.Revision + 1, EvidenceDigest = Digest(next) };
        await _store.SaveAsync(next, current.Revision, ct);
        return new DeepBookProofStepResult(next, false, next.Status == DeepBookProofStatus.Ready);
    }

    public async ValueTask<DeepBookProofCheckpoint> RecordRepairAsync(
        DeepBookProofRequest request,
        string reason,
        decimal repairCost,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        var current = await _store.LoadAsync(request.WorkspaceId, request.ProofId, ct)
            ?? throw new KeyNotFoundException("Proof checkpoint was not found.");
        var attempts = current.RepairAttempts + 1;
        var cost = current.AccumulatedCost + repairCost;
        if (attempts > request.Policy.MaximumRepairAttempts || cost > request.Policy.MaximumCost)
        {
            var blocked = Advance(current, DeepBookProofStatus.WaitingForDecision, current.Phase, cost, attempts,
                current.Artifacts, current.CompletedPhases, reason, at, current.ImageArtifacts);
            await _store.SaveAsync(blocked, current.Revision, ct);
            return blocked;
        }

        var repaired = Advance(current, DeepBookProofStatus.Active, current.Phase, cost, attempts,
            current.Artifacts, current.CompletedPhases, null, at, current.ImageArtifacts);
        await _store.SaveAsync(repaired, current.Revision, ct);
        return repaired;
    }

    private DeepBookProofCheckpoint VerifyAndFinalize(DeepBookProofCheckpoint current, DeepBookProofPolicy policy, decimal cost, DateTimeOffset at)
    {
        var required = policy.RequiredFormats.Count == 0 ? CanonicalFormats.ToHashSet(StringComparer.OrdinalIgnoreCase) : policy.RequiredFormats;
        var verified = current.Artifacts.Select(VerifyArtifact).ToArray();
        var formats = verified.Where(x => x.Verified).Select(x => x.Format).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = required.Where(x => !formats.Contains(x)).ToArray();
        if (missing.Length > 0)
            return Advance(current, DeepBookProofStatus.Failed, DeepBookProofPhase.ArtifactVerification, cost,
                current.RepairAttempts, verified, current.CompletedPhases, "Missing verified formats: " + string.Join(", ", missing), at, current.ImageArtifacts);

        if (policy.RequireImageEvidence && !HasValidImageEvidence(current.ImageArtifacts))
            return Advance(current, DeepBookProofStatus.Failed, DeepBookProofPhase.ArtifactVerification, cost,
                current.RepairAttempts, verified, current.CompletedPhases, "Missing or invalid image rights, provenance or accessibility evidence.", at, current.ImageArtifacts);

        var completed = current.CompletedPhases.Append(DeepBookProofPhase.ArtifactVerification)
            .Append(DeepBookProofPhase.PublicationReady).ToHashSet();
        return Advance(current, DeepBookProofStatus.Ready, DeepBookProofPhase.PublicationReady, cost,
            current.RepairAttempts, verified, completed, null, at, current.ImageArtifacts);
    }

    private bool HasValidImageEvidence(IReadOnlyList<ImageArtifactEvidence>? images)
    {
        if (images is not { Count: > 0 }) return false;
        foreach (var image in images)
        {
            var path = Path.GetFullPath(Path.Combine(_artifactRoot, image.RelativePath));
            if (!path.StartsWith(_artifactRoot, StringComparison.Ordinal) || !File.Exists(path)) return false;
            var bytes = File.ReadAllBytes(path);
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (bytes.LongLength == 0 || bytes.LongLength != image.ByteSize || !StringComparer.Ordinal.Equals(digest, image.Sha256)) return false;
            if (string.IsNullOrWhiteSpace(image.Provenance.ProviderId) || string.IsNullOrWhiteSpace(image.Provenance.Model) ||
                string.IsNullOrWhiteSpace(image.Provenance.ProviderRequestId) || string.IsNullOrWhiteSpace(image.Provenance.PromptDigest) ||
                string.IsNullOrWhiteSpace(image.Rights.LicenseKind) || string.IsNullOrWhiteSpace(image.Rights.LicenseReference) ||
                string.IsNullOrWhiteSpace(image.Rights.RightsHolder) || string.IsNullOrWhiteSpace(image.Rights.Territory) ||
                string.IsNullOrWhiteSpace(image.Accessibility.AltText)) return false;
        }
        return true;
    }

    private DeepBookArtifact VerifyArtifact(DeepBookArtifact artifact)
    {
        var path = Path.GetFullPath(Path.Combine(_artifactRoot, artifact.RelativePath));
        if (!path.StartsWith(_artifactRoot, StringComparison.Ordinal) || !File.Exists(path))
            return artifact with { Verified = false };
        var bytes = File.ReadAllBytes(path);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return artifact with { Verified = bytes.LongLength > 0 && bytes.LongLength == artifact.ByteSize && StringComparer.Ordinal.Equals(digest, artifact.Sha256) };
    }

    private static DeepBookProofCheckpoint AddArtifacts(DeepBookProofCheckpoint current, IReadOnlyList<DeepBookArtifact> produced,
        IReadOnlyList<ImageArtifactEvidence>? producedImages, decimal cost, DateTimeOffset at)
    {
        var merged = current.Artifacts.Concat(produced)
            .GroupBy(x => x.Format, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Last() with { Verified = false }).ToArray();
        var images = (current.ImageArtifacts ?? Array.Empty<ImageArtifactEvidence>()).Concat(producedImages ?? Array.Empty<ImageArtifactEvidence>())
            .GroupBy(x => x.AssetId).Select(x => x.Last()).ToArray();
        var completed = current.CompletedPhases.Append(DeepBookProofPhase.ArtifactProduction).ToHashSet();
        return Advance(current, DeepBookProofStatus.Active, DeepBookProofPhase.ArtifactVerification, cost,
            current.RepairAttempts, merged, completed, null, at, images);
    }

    private static DeepBookProofCheckpoint CompletePhase(DeepBookProofCheckpoint current, DeepBookProofPhase completed,
        DeepBookProofPhase next, decimal cost, DateTimeOffset at)
    {
        var phases = current.CompletedPhases.Append(completed).ToHashSet();
        return Advance(current, DeepBookProofStatus.Active, next, cost, current.RepairAttempts, current.Artifacts, phases, null, at, current.ImageArtifacts);
    }

    private static DeepBookProofCheckpoint NewCheckpoint(DeepBookProofRequest request, DateTimeOffset at)
    {
        var checkpoint = new DeepBookProofCheckpoint(request.ProofId, request.JourneyId, request.WorkspaceId,
            DeepBookProofStatus.Active, DeepBookProofPhase.Intake, 1, 0m, 0, Array.Empty<DeepBookArtifact>(),
            new HashSet<DeepBookProofPhase>(), string.Empty, null, at, Array.Empty<ImageArtifactEvidence>());
        return checkpoint with { EvidenceDigest = Digest(checkpoint) };
    }

    private static DeepBookProofCheckpoint Advance(DeepBookProofCheckpoint current, DeepBookProofStatus status,
        DeepBookProofPhase phase, decimal cost, int repairs, IReadOnlyList<DeepBookArtifact> artifacts,
        IReadOnlySet<DeepBookProofPhase> completed, string? blockingReason, DateTimeOffset at,
        IReadOnlyList<ImageArtifactEvidence>? images)
    {
        var next = current with
        {
            Status = status,
            Phase = phase,
            Revision = current.Revision + 1,
            AccumulatedCost = cost,
            RepairAttempts = repairs,
            Artifacts = artifacts,
            CompletedPhases = completed.ToHashSet(),
            BlockingReason = blockingReason,
            UpdatedAtUtc = at,
            ImageArtifacts = images ?? Array.Empty<ImageArtifactEvidence>()
        };
        return next with { EvidenceDigest = Digest(next) };
    }

    private static void ValidateRequest(DeepBookProofRequest request, BookCreationJourney journey, decimal stepCost)
    {
        if (request.ProofId == Guid.Empty || request.JourneyId == Guid.Empty || string.IsNullOrWhiteSpace(request.WorkspaceId) ||
            string.IsNullOrWhiteSpace(request.NaturalLanguageIdea) || string.IsNullOrWhiteSpace(request.Actor))
            throw new DeepBookProofValidationException("Proof identity, workspace, idea and actor are required.");
        if (request.JourneyId != journey.JourneyId || !StringComparer.Ordinal.Equals(request.WorkspaceId, journey.WorkspaceId))
            throw new DeepBookProofValidationException("Journey does not belong to the proof workspace.");
        if (request.Policy.MaximumCost < 0 || request.Policy.MaximumRepairAttempts < 0 || stepCost < 0)
            throw new DeepBookProofValidationException("Cost and repair limits cannot be negative.");
    }

    private static string Digest(DeepBookProofCheckpoint checkpoint)
    {
        var artifacts = string.Join(';', checkpoint.Artifacts.OrderBy(x => x.Format, StringComparer.Ordinal)
            .Select(x => $"{x.Format}|{x.RelativePath}|{x.MediaType}|{x.ByteSize}|{x.Sha256}|{x.Provenance}|{x.Verified}"));
        var images = string.Join(';', (checkpoint.ImageArtifacts ?? Array.Empty<ImageArtifactEvidence>()).OrderBy(x => x.AssetId)
            .Select(x => $"{x.AssetId}|{x.RelativePath}|{x.MediaType}|{x.ByteSize}|{x.Sha256}|{x.Provenance.ProviderId}|{x.Provenance.Model}|{x.Provenance.ProviderRequestId}|{x.Provenance.PromptDigest}|{x.Rights.LicenseKind}|{x.Rights.LicenseReference}|{x.Rights.RightsHolder}|{x.Rights.Territory}|{x.Accessibility.AltText}|{x.ChargedCost}|{x.Currency}"));
        var phases = string.Join(',', checkpoint.CompletedPhases.OrderBy(x => x));
        var value = $"{checkpoint.ProofId}|{checkpoint.JourneyId}|{checkpoint.WorkspaceId}|{checkpoint.Status}|{checkpoint.Phase}|{checkpoint.Revision}|{checkpoint.AccumulatedCost}|{checkpoint.RepairAttempts}|{artifacts}|{images}|{phases}|{checkpoint.BlockingReason}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}

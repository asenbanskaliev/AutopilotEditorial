using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BookStudio.Application.Publishing;

public sealed class ProfessionalReleaseOrchestrator
{
    private static readonly Regex SemanticVersionPattern = new("^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);
    private readonly IProfessionalReleaseStore _store;
    private readonly IProofReleaseAuthorityReader _authority;
    private readonly IReleaseArtifactReader _artifacts;

    public ProfessionalReleaseOrchestrator(IProfessionalReleaseStore store,
        IProofReleaseAuthorityReader authority, IReleaseArtifactReader artifacts)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
    }

    public async ValueTask<ProfessionalReleaseState> SubmitAsync(ProfessionalReleaseRequest request,
        DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(request);
        var snapshot = await _authority.RequireCurrentAsync(request.Authority, ct);
        RequireApprovedCurrent(snapshot, request.Authority);
        return (await _store.SubmitAsync(request, at, ct)).State;
    }

    public async ValueTask<ProfessionalReleaseState> FreezeAsync(ProfessionalReleaseFreezeCommand command,
        DateTimeOffset at, CancellationToken ct = default)
    {
        RequireText(command.WorkspaceId, command.Actor, command.RequestFingerprint);
        var state = await RequireAsync(command.WorkspaceId, command.ReleaseId, ct);
        RequireRevision(state, command.ExpectedRevision);
        if (state.Status != ProfessionalReleaseStatus.Draft)
            throw new ProfessionalReleaseTransitionException("Only a draft professional release can be frozen.");

        var authority = await _authority.RequireCurrentAsync(state.Authority, ct);
        RequireApprovedCurrent(authority, state.Authority);

        var requested = state.Manifest?.Artifacts ?? Array.Empty<VerifiedReleaseArtifact>();
        if (requested.Count != 0)
            throw new ProfessionalReleaseTransitionException("A draft release cannot already contain a frozen manifest.");

        var submitted = await LoadRequestArtifactsAsync(state, ct);
        var verified = submitted.OrderBy(x => x.LogicalName, StringComparer.Ordinal)
            .ThenBy(x => x.MediaType, StringComparer.Ordinal)
            .ThenBy(x => x.Digest, StringComparer.Ordinal)
            .ToArray();
        RequireRequiredInventory(verified);

        var inventoryDigest = Hash(string.Join("\n", verified.Select(CanonicalArtifact)));
        var manifestDigest = Hash($"{state.ReleaseId:D}|{state.Channel}|{state.SemanticVersion}|{state.Locale}|{inventoryDigest}");
        var manifest = new ProfessionalReleaseManifest(state.ReleaseId, state.Channel, state.SemanticVersion,
            state.Locale, verified, manifestDigest, at);
        var evidenceDigest = Hash($"{state.Authority.ProofEvidenceDigest}|{state.Authority.PackageDigest}|{manifest.ManifestDigest}|{inventoryDigest}");

        return await _store.FreezeAsync(command, verified, manifest, inventoryDigest, evidenceDigest, at, ct);
    }

    public async ValueTask<ProfessionalReleaseState> DecideAsync(ProfessionalReleaseDecisionCommand command,
        DateTimeOffset at, CancellationToken ct = default)
    {
        RequireText(command.WorkspaceId, command.Reason, command.Evidence, command.EvidenceDigest,
            command.Actor, command.RequestFingerprint);
        var state = await RequireAsync(command.WorkspaceId, command.ReleaseId, ct);
        RequireRevision(state, command.ExpectedRevision);

        if (command.Decision == ProfessionalReleaseDecision.Approve)
        {
            if (state.Status != ProfessionalReleaseStatus.Frozen || state.Manifest is null ||
                string.IsNullOrWhiteSpace(state.InventoryDigest) || string.IsNullOrWhiteSpace(state.EvidenceDigest))
                throw new ProfessionalReleaseTransitionException("Only a completely frozen release can be approved.");
            if (!StringComparer.Ordinal.Equals(command.EvidenceDigest, state.EvidenceDigest))
                throw new ProfessionalReleaseValidationException("Approval evidence does not match the frozen release evidence.");
        }

        if (command.Decision == ProfessionalReleaseDecision.Supersede && state.Status != ProfessionalReleaseStatus.Approved)
            throw new ProfessionalReleaseTransitionException("Only an approved release can be superseded.");

        return await _store.DecideAsync(command, at, ct);
    }

    private async ValueTask<IReadOnlyList<VerifiedReleaseArtifact>> LoadRequestArtifactsAsync(
        ProfessionalReleaseState state, CancellationToken ct)
    {
        if (state.Artifacts.Count == 0)
            throw new ProfessionalReleaseValidationException("At least one release artifact is required.");

        var result = new List<VerifiedReleaseArtifact>(state.Artifacts.Count);
        foreach (var expected in state.Artifacts)
        {
            ValidateArtifact(expected);
            var actual = await _artifacts.ReadAsync(new ReleaseArtifactReference(expected.LogicalName,
                expected.MediaType, expected.ByteLength, expected.Digest, expected.Provenance,
                expected.SourceAuthority, expected.Required), ct);
            if (!StringComparer.Ordinal.Equals(actual.LogicalName, expected.LogicalName) ||
                !StringComparer.Ordinal.Equals(actual.MediaType, expected.MediaType) ||
                actual.ByteLength != expected.ByteLength ||
                !StringComparer.Ordinal.Equals(actual.Digest, expected.Digest) ||
                !StringComparer.Ordinal.Equals(actual.Provenance, expected.Provenance) ||
                !StringComparer.Ordinal.Equals(actual.SourceAuthority, expected.SourceAuthority))
                throw new ProfessionalReleaseValidationException($"Release artifact '{expected.LogicalName}' does not match its declared authority.");

            var digest = Convert.ToHexString(SHA256.HashData(actual.Content.Span)).ToLowerInvariant();
            if (actual.Content.Length != expected.ByteLength || !StringComparer.Ordinal.Equals(digest, expected.Digest))
                throw new ProfessionalReleaseValidationException($"Release artifact '{expected.LogicalName}' failed digest or length verification.");
            result.Add(expected);
        }
        return result;
    }

    private static void ValidateRequest(ProfessionalReleaseRequest request)
    {
        if (request.RequestId == Guid.Empty || request.ReleaseId == Guid.Empty || request.ProjectId == Guid.Empty ||
            request.Authority.ProofId == Guid.Empty || request.Authority.ProofRevision <= 0 || request.Authority.PackageId == Guid.Empty)
            throw new ProfessionalReleaseValidationException("Stable release, proof and package identities are required.");
        RequireText(request.WorkspaceId, request.Authority.ProofEvidenceDigest, request.Authority.PackageDigest,
            request.Channel, request.SemanticVersion, request.Locale, request.Actor, request.RequestFingerprint);
        if (!SemanticVersionPattern.IsMatch(request.SemanticVersion))
            throw new ProfessionalReleaseValidationException("Professional release semantic version is invalid.");
        if (!StringComparer.Ordinal.Equals(request.WorkspaceId, request.Authority.WorkspaceId) ||
            request.ProjectId != request.Authority.ProjectId)
            throw new ProfessionalReleaseValidationException("Cross-workspace or cross-project proof authority is forbidden.");
        if (request.SupersedesReleaseId == request.ReleaseId)
            throw new ProfessionalReleaseValidationException("A release cannot supersede itself.");
        if (request.Artifacts.Count == 0)
            throw new ProfessionalReleaseValidationException("At least one release artifact is required.");
        var duplicate = request.Artifacts.GroupBy(x => x.LogicalName, StringComparer.Ordinal)
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
            throw new ProfessionalReleaseValidationException($"Duplicate release artifact logical name '{duplicate.Key}'.");
        foreach (var artifact in request.Artifacts) ValidateArtifact(artifact);
    }

    private static void ValidateArtifact(VerifiedReleaseArtifact artifact)
    {
        RequireText(artifact.LogicalName, artifact.MediaType, artifact.Digest, artifact.Provenance, artifact.SourceAuthority);
        if (artifact.ByteLength < 0 || artifact.Digest.Length != 64 || artifact.Digest.Any(c => !Uri.IsHexDigit(c)))
            throw new ProfessionalReleaseValidationException("Release artifact length or SHA-256 digest is invalid.");
    }

    private static void ValidateArtifact(ReleaseArtifactReference artifact) =>
        ValidateArtifact(new VerifiedReleaseArtifact(artifact.LogicalName, artifact.MediaType, artifact.ByteLength,
            artifact.Digest, artifact.Provenance, artifact.SourceAuthority, artifact.Required));

    private static void RequireRequiredInventory(IReadOnlyList<VerifiedReleaseArtifact> artifacts)
    {
        string[] required = ["manuscript", "cover", "metadata", "accessibility", "preflight", "proof"];
        foreach (var category in required)
        {
            if (!artifacts.Any(x => x.Required && x.LogicalName.Contains(category, StringComparison.OrdinalIgnoreCase)))
                throw new ProfessionalReleaseValidationException($"Required professional release artifact category '{category}' is missing.");
        }
    }

    private static string CanonicalArtifact(VerifiedReleaseArtifact value) =>
        $"{value.LogicalName}|{value.MediaType}|{value.ByteLength}|{value.Digest}|{value.Provenance}|{value.SourceAuthority}|{value.Required}";

    private static void RequireApprovedCurrent(ProofReleaseAuthoritySnapshot snapshot, ProofReleaseAuthority requested)
    {
        if (!snapshot.IsCurrent || requested.Status != ProofReleaseAuthorityStatus.Approved ||
            snapshot.Authority.ProofId != requested.ProofId ||
            snapshot.Authority.ProofRevision != requested.ProofRevision ||
            !StringComparer.Ordinal.Equals(snapshot.Authority.ProofEvidenceDigest, requested.ProofEvidenceDigest) ||
            snapshot.Authority.PackageId != requested.PackageId ||
            !StringComparer.Ordinal.Equals(snapshot.Authority.PackageDigest, requested.PackageDigest))
            throw new ProfessionalReleaseValidationException("VS-117 proof authority is stale, mismatched or not approved.");
    }

    private async ValueTask<ProfessionalReleaseState> RequireAsync(string workspaceId, Guid releaseId, CancellationToken ct) =>
        await _store.GetAsync(workspaceId, releaseId, ct) ??
        throw new ProfessionalReleaseValidationException("Professional release not found.");

    private static void RequireRevision(ProfessionalReleaseState state, long expected)
    {
        if (state.Revision != expected)
            throw new ProfessionalReleaseConflictException("Stale professional release revision.");
    }

    private static void RequireText(params string[] values)
    {
        if (values.Any(string.IsNullOrWhiteSpace))
            throw new ProfessionalReleaseValidationException("Required professional release text is missing.");
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

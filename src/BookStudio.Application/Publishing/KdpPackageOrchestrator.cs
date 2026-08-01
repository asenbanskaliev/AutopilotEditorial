using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BookStudio.Application.Publishing;

public sealed class KdpPackageOrchestrator
{
    private static readonly DateTimeOffset StableZipTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly IKdpPackageStore _store;
    private readonly IKdpPackageAuthorityReader _authority;
    private readonly IKdpArtifactReader _artifacts;

    public KdpPackageOrchestrator(IKdpPackageStore store, IKdpPackageAuthorityReader authority, IKdpArtifactReader artifacts)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
    }

    public async ValueTask<KdpPackageState> SubmitAsync(KdpPackageRequest request, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(request);
        var authority = await _authority.RequireCurrentAsync(request.Authority, ct);
        if (!authority.IsCurrent || request.Authority.Status != KdpPackageAuthorityStatus.Approved)
            throw new KdpPackageValidationException("VS-115 authority is stale, mismatched or not approved.");
        return (await _store.SubmitAsync(request, at, ct)).State;
    }

    public async ValueTask<KdpPackageState> EvaluateAsync(KdpPackageEvaluationCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        var state = await RequireAsync(command.WorkspaceId, command.PackageId, ct);
        RequireRevision(state, command.ExpectedRevision);
        var authority = await _authority.RequireCurrentAsync(state.Authority, ct);
        if (!authority.IsCurrent || state.Authority.Status != KdpPackageAuthorityStatus.Approved)
            throw new KdpPackageValidationException("VS-115 authority drifted before KDP package evaluation.");

        var findings = ValidateMetadata(state).ToArray();
        var snapshots = new List<KdpArtifactSnapshot>(state.Artifacts.Count);
        foreach (var artifact in state.Artifacts.OrderBy(x => NormalizePath(x.Path), StringComparer.Ordinal))
        {
            var snapshot = await _artifacts.ReadAsync(artifact, ct);
            var actual = Convert.ToHexString(SHA256.HashData(snapshot.Content.Span)).ToLowerInvariant();
            if (!StringComparer.Ordinal.Equals(actual, artifact.Sha256Digest) ||
                !StringComparer.Ordinal.Equals(actual, snapshot.VerifiedSha256Digest) || snapshot.Content.Length != artifact.ByteLength)
                throw new KdpPackageValidationException($"Artifact digest or length mismatch: {artifact.Path}.");
            snapshots.Add(snapshot);
        }

        var manifest = BuildManifest(snapshots);
        var evidence = BuildEvidenceDigest(state, manifest, findings);
        return await _store.EvaluateAsync(command, manifest, findings, evidence, at, ct);
    }

    public async ValueTask<KdpPackageState> DecideAsync(KdpPackageDecisionCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        var state = await RequireAsync(command.WorkspaceId, command.PackageId, ct);
        RequireRevision(state, command.ExpectedRevision);
        RequireText(command.Reason, command.Evidence, command.EvidenceDigest, command.Actor, command.RequestFingerprint);
        if (command.Decision == KdpPackageDecision.Approve)
        {
            if (state.Status != KdpPackageStatus.Evaluated || state.Manifest is null || string.IsNullOrWhiteSpace(state.EvidenceDigest))
                throw new KdpPackageTransitionException("Only an evaluated package with immutable evidence can be approved.");
            if (state.Findings.Any(x => x.Severity == KdpFindingSeverity.Blocking && x.Status == KdpFindingStatus.Open))
                throw new KdpPackageTransitionException("Open blocking metadata findings prevent approval.");
        }
        if (command.Decision == KdpPackageDecision.Supersede && state.Status != KdpPackageStatus.Approved)
            throw new KdpPackageTransitionException("Only an approved KDP package can be superseded.");
        return await _store.DecideAsync(command, at, ct);
    }

    public static KdpPackageManifest BuildManifest(IEnumerable<KdpArtifactSnapshot> snapshots)
    {
        var ordered = snapshots.Select(x => new KdpManifestEntry(NormalizePath(x.Artifact.Path), x.Artifact.MediaType,
                x.Artifact.ByteLength, x.Artifact.Sha256Digest, x.Artifact.Kind))
            .OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0 || ordered.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new KdpPackageValidationException("Package entries must be non-empty and uniquely addressed.");

        var canonical = JsonSerializer.Serialize(ordered, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var manifestDigest = Hash(canonical);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in snapshots.OrderBy(x => NormalizePath(x.Artifact.Path), StringComparer.Ordinal))
            {
                var entry = zip.CreateEntry(NormalizePath(item.Artifact.Path), CompressionLevel.NoCompression);
                entry.LastWriteTime = StableZipTimestamp;
                using var stream = entry.Open();
                stream.Write(item.Content.Span);
            }
            var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression);
            manifestEntry.LastWriteTime = StableZipTimestamp;
            using var manifestStream = manifestEntry.Open();
            manifestStream.Write(Encoding.UTF8.GetBytes(canonical));
        }
        return new KdpPackageManifest(ordered, canonical, manifestDigest,
            Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant());
    }

    private static IEnumerable<KdpMetadataFinding> ValidateMetadata(KdpPackageState state)
    {
        var findings = new List<KdpMetadataFinding>();
        AddRequired(findings, state.Metadata.Title, "title", "KDP-META-001");
        AddRequired(findings, state.Metadata.Description, "description", "KDP-META-002");
        AddRequired(findings, state.Metadata.Language, "language", "KDP-META-003");
        AddRequired(findings, state.Metadata.PricingIntent, "pricingIntent", "KDP-META-004");
        if (state.Metadata.Contributors.Count == 0)
            Add(findings, "contributors", "KDP-META-005", "At least one contributor is required.");
        if (state.Metadata.Categories.Count == 0)
            Add(findings, "categories", "KDP-META-006", "At least one category is required.");
        if (state.Metadata.Territories.Count == 0)
            Add(findings, "territories", "KDP-META-007", "Publication territories are required.");
        if ((state.Metadata.AiDisclosure.ContainsAiGeneratedText || state.Metadata.AiDisclosure.ContainsAiGeneratedImages) &&
            string.IsNullOrWhiteSpace(state.Metadata.AiDisclosure.Evidence))
            Add(findings, "aiDisclosure", "KDP-META-008", "AI-generated content requires explicit disclosure evidence.");
        if (!StringComparer.OrdinalIgnoreCase.Equals(state.Language, state.Metadata.Language))
            Add(findings, "language", "KDP-META-009", "Package and metadata language must match.");
        return findings;
    }

    private static void AddRequired(List<KdpMetadataFinding> findings, string? value, string field, string code)
    {
        if (string.IsNullOrWhiteSpace(value)) Add(findings, field, code, $"Required metadata field '{field}' is missing.");
    }

    private static void Add(List<KdpMetadataFinding> findings, string field, string code, string description)
    {
        var evidence = Hash($"{code}|{field}|{description}");
        findings.Add(new KdpMetadataFinding(DeterministicGuid(evidence), code, KdpFindingSeverity.Blocking,
            field, code, description, evidence, KdpFindingStatus.Open));
    }

    private static string BuildEvidenceDigest(KdpPackageState state, KdpPackageManifest manifest, IEnumerable<KdpMetadataFinding> findings)
    {
        var canonicalFindings = string.Join("\n", findings.OrderBy(x => x.Code, StringComparer.Ordinal)
            .Select(x => $"{x.FindingId:D}|{x.Code}|{x.Severity}|{x.Field}|{x.RuleId}|{x.EvidenceDigest}|{x.Status}"));
        return Hash($"{state.Authority.TechnicalPreflightEvidenceDigest}\n{state.Marketplace}\n{state.Language}\n{state.FormatProfile}\n{state.ProfileVersion}\n{manifest.ManifestDigest}\n{manifest.PackageDigest}\n{canonicalFindings}");
    }

    private async ValueTask<KdpPackageState> RequireAsync(string workspaceId, Guid packageId, CancellationToken ct) =>
        await _store.GetAsync(workspaceId, packageId, ct) ?? throw new KdpPackageValidationException("KDP package not found.");

    private static void ValidateRequest(KdpPackageRequest request)
    {
        if (request.RequestId == Guid.Empty || request.PackageId == Guid.Empty || request.ProjectId == Guid.Empty ||
            request.Authority.TechnicalPreflightRunId == Guid.Empty || request.Authority.TechnicalPreflightRevision <= 0)
            throw new KdpPackageValidationException("Stable KDP package identity is required.");
        RequireText(request.WorkspaceId, request.Authority.TechnicalPreflightEvidenceDigest, request.Marketplace,
            request.Language, request.FormatProfile, request.ProfileVersion, request.Actor, request.RequestFingerprint);
        if (!StringComparer.Ordinal.Equals(request.WorkspaceId, request.Authority.WorkspaceId) || request.ProjectId != request.Authority.ProjectId)
            throw new KdpPackageValidationException("Cross-workspace or cross-project authority is forbidden.");
        if (request.Artifacts.Count == 0 || !request.Artifacts.Any(x => x.Kind == KdpArtifactKind.Manuscript) ||
            !request.Artifacts.Any(x => x.Kind == KdpArtifactKind.Cover))
            throw new KdpPackageValidationException("A manuscript and cover are required.");
        foreach (var artifact in request.Artifacts)
        {
            NormalizePath(artifact.Path);
            RequireText(artifact.Path, artifact.MediaType, artifact.Sha256Digest);
            if (artifact.ByteLength < 0) throw new KdpPackageValidationException("Artifact length cannot be negative.");
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new KdpPackageValidationException("Package path is required.");
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 || normalized.Split('/').Any(x => x is "" or "." or "..") || Path.IsPathRooted(path))
            throw new KdpPackageValidationException($"Unsafe package path: {path}.");
        return normalized;
    }

    private static void RequireRevision(KdpPackageState state, long expected)
    {
        if (state.Revision != expected) throw new KdpPackageConflictException("Stale KDP package revision.");
    }

    private static void RequireText(params string[] values)
    {
        if (values.Any(string.IsNullOrWhiteSpace)) throw new KdpPackageValidationException("Required KDP package text is missing.");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Guid DeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

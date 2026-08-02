using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BookStudio.Application.Authoring;

public sealed record ImageGenerationPolicy(
    decimal MaxCost,
    string Currency,
    int MaxRepairAttempts,
    IReadOnlySet<string> AllowedLicenseKinds,
    string RequiredTerritory);

public sealed record ImageGenerationRequest(
    string WorkspaceId,
    Guid AssetId,
    string Prompt,
    string AltText,
    int Width,
    int Height,
    ImageGenerationPolicy Policy);

public sealed record ImageProviderQuote(decimal Cost, string Currency);

public sealed record ImageProviderOutput(
    byte[] Bytes,
    string MediaType,
    string Model,
    string ProviderRequestId,
    decimal Cost,
    string Currency,
    string LicenseKind,
    string LicenseReference,
    string RightsHolder,
    string Territory,
    DateTimeOffset CapturedAtUtc);

public interface IImageGenerationProvider
{
    string ProviderId { get; }
    ValueTask<ImageProviderQuote> QuoteAsync(ImageGenerationRequest request, CancellationToken ct = default);
    ValueTask<ImageProviderOutput> GenerateAsync(ImageGenerationRequest request, CancellationToken ct = default);
}

public sealed record ImageArtifactEvidence(
    Guid AssetId,
    string RelativePath,
    string MediaType,
    long ByteSize,
    string Sha256,
    AssetProvenanceEvidence Provenance,
    AssetRightsEvidence Rights,
    AssetAccessibilityEvidence Accessibility,
    decimal ChargedCost,
    string Currency,
    int RepairAttempts,
    bool ReusedExistingArtifact);

public sealed class ImageProviderRightsPipeline
{
    private readonly IImageGenerationProvider _provider;
    private readonly string _workspaceRoot;

    public ImageProviderRightsPipeline(IImageGenerationProvider provider, string workspaceRoot)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        Directory.CreateDirectory(_workspaceRoot);
    }

    public async ValueTask<ImageArtifactEvidence> ExecuteAsync(ImageGenerationRequest request, CancellationToken ct = default)
    {
        Validate(request);
        var directory = ConfinedPath(Path.Combine("images", request.AssetId.ToString("N")));
        Directory.CreateDirectory(directory);
        var artifactPath = ConfinedPath(Path.Combine("images", request.AssetId.ToString("N"), "asset.svg"));
        var evidencePath = artifactPath + ".evidence.json";

        if (File.Exists(artifactPath) && File.Exists(evidencePath))
        {
            var existing = JsonSerializer.Deserialize<ImageArtifactEvidence>(await File.ReadAllTextAsync(evidencePath, ct));
            if (existing is not null && existing.AssetId == request.AssetId && existing.Sha256 == Sha256(await File.ReadAllBytesAsync(artifactPath, ct)))
                return existing with { ReusedExistingArtifact = true };
        }

        var quote = await _provider.QuoteAsync(request, ct);
        EnforceCost(quote.Cost, quote.Currency, request.Policy);

        Exception? lastFailure = null;
        for (var attempt = 0; attempt <= request.Policy.MaxRepairAttempts; attempt++)
        {
            try
            {
                var output = await _provider.GenerateAsync(request, ct);
                EnforceCost(output.Cost, output.Currency, request.Policy);
                if (!string.Equals(output.MediaType, "image/svg+xml", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Provider output must be image/svg+xml.");
                if (output.Bytes.Length == 0)
                    throw new InvalidOperationException("Provider returned an empty image.");
                if (!request.Policy.AllowedLicenseKinds.Contains(output.LicenseKind))
                    throw new InvalidOperationException("Provider license is not allowed by policy.");
                if (!string.Equals(output.Territory, request.Policy.RequiredTerritory, StringComparison.OrdinalIgnoreCase) && output.Territory != "WORLDWIDE")
                    throw new InvalidOperationException("Provider rights territory does not satisfy policy.");
                if (string.IsNullOrWhiteSpace(output.LicenseReference) || string.IsNullOrWhiteSpace(output.RightsHolder) || string.IsNullOrWhiteSpace(output.ProviderRequestId))
                    throw new InvalidOperationException("Provider rights and provenance evidence are incomplete.");

                await AtomicWriteAsync(artifactPath, output.Bytes, ct);
                var digest = Sha256(output.Bytes);
                var promptDigest = Sha256(Encoding.UTF8.GetBytes(request.Prompt));
                var provenanceDigest = Sha256(Encoding.UTF8.GetBytes($"{_provider.ProviderId}|{output.Model}|{output.ProviderRequestId}|{promptDigest}|{digest}"));
                var rightsDigest = Sha256(Encoding.UTF8.GetBytes($"{output.LicenseKind}|{output.LicenseReference}|{output.RightsHolder}|{output.Territory}|{digest}"));
                var accessibilityDigest = Sha256(Encoding.UTF8.GetBytes($"{request.AltText}|{digest}"));

                var evidence = new ImageArtifactEvidence(
                    request.AssetId,
                    Path.GetRelativePath(_workspaceRoot, artifactPath).Replace('\\', '/'),
                    output.MediaType,
                    output.Bytes.LongLength,
                    digest,
                    new AssetProvenanceEvidence(_provider.ProviderId, output.Model, output.ProviderRequestId, promptDigest, "[]", provenanceDigest, output.CapturedAtUtc),
                    new AssetRightsEvidence(output.LicenseKind, output.LicenseReference, output.RightsHolder, output.Territory, output.CapturedAtUtc, null, rightsDigest),
                    new AssetAccessibilityEvidence(request.AltText, request.AltText, "en", accessibilityDigest),
                    output.Cost,
                    output.Currency,
                    attempt,
                    false);

                await AtomicWriteAsync(evidencePath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true })), ct);
                return evidence;
            }
            catch (Exception ex) when (attempt < request.Policy.MaxRepairAttempts && ex is not OperationCanceledException)
            {
                lastFailure = ex;
            }
        }

        throw new InvalidOperationException("Image provider repair ceiling exhausted.", lastFailure);
    }

    private void Validate(ImageGenerationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceId) || string.IsNullOrWhiteSpace(request.Prompt) || string.IsNullOrWhiteSpace(request.AltText))
            throw new ArgumentException("Workspace, prompt and alt text are required.");
        if (request.Width <= 0 || request.Height <= 0 || request.Policy.MaxCost < 0 || request.Policy.MaxRepairAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Policy.AllowedLicenseKinds.Count == 0 || string.IsNullOrWhiteSpace(request.Policy.Currency) || string.IsNullOrWhiteSpace(request.Policy.RequiredTerritory))
            throw new ArgumentException("A complete rights and cost policy is required.");
    }

    private static void EnforceCost(decimal cost, string currency, ImageGenerationPolicy policy)
    {
        if (cost < 0 || cost > policy.MaxCost || !string.Equals(currency, policy.Currency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Provider cost violates policy.");
    }

    private string ConfinedPath(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_workspaceRoot, relative));
        var prefix = _workspaceRoot.EndsWith(Path.DirectorySeparatorChar) ? _workspaceRoot : _workspaceRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Image path escapes the workspace.");
        return path;
    }

    private static async ValueTask AtomicWriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed class DeterministicLicensedSvgProvider : IImageGenerationProvider
{
    public string ProviderId => "deterministic-licensed-svg";

    public ValueTask<ImageProviderQuote> QuoteAsync(ImageGenerationRequest request, CancellationToken ct = default)
        => ValueTask.FromResult(new ImageProviderQuote(0m, request.Policy.Currency));

    public ValueTask<ImageProviderOutput> GenerateAsync(ImageGenerationRequest request, CancellationToken ct = default)
    {
        var escapedPrompt = System.Security.SecurityElement.Escape(request.Prompt) ?? string.Empty;
        var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{request.Width}\" height=\"{request.Height}\" viewBox=\"0 0 {request.Width} {request.Height}\"><rect width=\"100%\" height=\"100%\" fill=\"white\"/><text x=\"24\" y=\"48\" font-family=\"sans-serif\" font-size=\"24\">{escapedPrompt}</text></svg>";
        return ValueTask.FromResult(new ImageProviderOutput(
            Encoding.UTF8.GetBytes(svg), "image/svg+xml", "reference-svg-v1",
            "req-" + request.AssetId.ToString("N"), 0m, request.Policy.Currency,
            "PROJECT_OWNED", "license-" + request.AssetId.ToString("N"), "AutopilotEditorial",
            "WORLDWIDE", DateTimeOffset.UnixEpoch));
    }
}
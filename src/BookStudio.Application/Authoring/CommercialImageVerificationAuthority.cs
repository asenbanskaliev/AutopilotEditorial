using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BookStudio.Application.Authoring;

public sealed record ImageModerationDecision(
    bool Approved,
    string DecisionId,
    string PolicyVersion,
    string ArtifactSha256,
    decimal Cost,
    string Currency,
    DateTimeOffset CapturedAtUtc,
    string? RejectionReason = null);

public sealed record ExternalRightsClearanceDecision(
    bool Cleared,
    string ClearanceId,
    string Registry,
    string ArtifactSha256,
    string LicenseReference,
    string RightsHolder,
    string Territory,
    decimal Cost,
    string Currency,
    DateTimeOffset CapturedAtUtc,
    string? RejectionReason = null);

public interface IImageSafetyModerator
{
    ValueTask<ImageModerationDecision> ModerateAsync(
        ImageGenerationRequest request,
        ImageProviderOutput output,
        string artifactSha256,
        CancellationToken ct = default);
}

public interface IExternalRightsClearanceService
{
    ValueTask<ExternalRightsClearanceDecision> VerifyAsync(
        ImageGenerationRequest request,
        ImageProviderOutput output,
        string artifactSha256,
        CancellationToken ct = default);
}

public sealed record CommercialImageVerificationEvidence(
    Guid AssetId,
    string ArtifactSha256,
    string ProviderId,
    string ProviderRequestId,
    ImageModerationDecision Moderation,
    ExternalRightsClearanceDecision RightsClearance,
    decimal TotalVerificationCost,
    string Currency,
    DateTimeOffset VerifiedAtUtc);

public sealed class CommercialImageVerificationAuthority : IImageGenerationProvider
{
    private readonly IImageGenerationProvider _inner;
    private readonly IImageSafetyModerator _moderator;
    private readonly IExternalRightsClearanceService _rightsClearance;
    private readonly string _workspaceRoot;

    public CommercialImageVerificationAuthority(
        IImageGenerationProvider inner,
        IImageSafetyModerator moderator,
        IExternalRightsClearanceService rightsClearance,
        string workspaceRoot)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _moderator = moderator ?? throw new ArgumentNullException(nameof(moderator));
        _rightsClearance = rightsClearance ?? throw new ArgumentNullException(nameof(rightsClearance));
        _workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        Directory.CreateDirectory(_workspaceRoot);
    }

    public string ProviderId => $"verified:{_inner.ProviderId}";

    public ValueTask<ImageProviderQuote> QuoteAsync(ImageGenerationRequest request, CancellationToken ct = default)
        => _inner.QuoteAsync(request, ct);

    public async ValueTask<ImageProviderOutput> GenerateAsync(ImageGenerationRequest request, CancellationToken ct = default)
    {
        var output = await _inner.GenerateAsync(request, ct);
        if (output.Bytes.Length == 0)
            throw new InvalidOperationException("Commercial provider returned an empty image.");

        var digest = Sha256(output.Bytes);
        var evidencePath = ConfinedPath(Path.Combine("commercial-image-verification", request.AssetId.ToString("N") + ".json"));
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);

        if (File.Exists(evidencePath))
        {
            var existing = JsonSerializer.Deserialize<CommercialImageVerificationEvidence>(await File.ReadAllTextAsync(evidencePath, ct));
            if (existing is not null &&
                existing.AssetId == request.AssetId &&
                existing.ArtifactSha256 == digest &&
                existing.ProviderId == _inner.ProviderId &&
                existing.ProviderRequestId == output.ProviderRequestId &&
                existing.Moderation.Approved &&
                existing.RightsClearance.Cleared)
            {
                return output with { Cost = output.Cost + existing.TotalVerificationCost };
            }
        }

        var moderation = await _moderator.ModerateAsync(request, output, digest, ct);
        ValidateModeration(moderation, digest, request.Policy.Currency);
        if (!moderation.Approved)
            throw new InvalidOperationException("Image moderation rejected the generated asset: " + (moderation.RejectionReason ?? "unspecified"));

        var clearance = await _rightsClearance.VerifyAsync(request, output, digest, ct);
        ValidateClearance(clearance, output, digest, request.Policy.Currency, request.Policy.RequiredTerritory);
        if (!clearance.Cleared)
            throw new InvalidOperationException("External rights clearance rejected the generated asset: " + (clearance.RejectionReason ?? "unspecified"));

        var verificationCost = moderation.Cost + clearance.Cost;
        var totalCost = output.Cost + verificationCost;
        if (totalCost > request.Policy.MaxCost)
            throw new InvalidOperationException("Provider plus verification cost violates policy.");

        var evidence = new CommercialImageVerificationEvidence(
            request.AssetId,
            digest,
            _inner.ProviderId,
            output.ProviderRequestId,
            moderation,
            clearance,
            verificationCost,
            request.Policy.Currency,
            DateTimeOffset.UtcNow);

        await AtomicWriteAsync(evidencePath, JsonSerializer.SerializeToUtf8Bytes(evidence, new JsonSerializerOptions { WriteIndented = true }), ct);
        return output with
        {
            Cost = totalCost,
            LicenseReference = clearance.LicenseReference,
            RightsHolder = clearance.RightsHolder,
            Territory = clearance.Territory
        };
    }

    private static void ValidateModeration(ImageModerationDecision decision, string digest, string currency)
    {
        if (string.IsNullOrWhiteSpace(decision.DecisionId) || string.IsNullOrWhiteSpace(decision.PolicyVersion))
            throw new InvalidOperationException("Moderation evidence is incomplete.");
        if (!string.Equals(decision.ArtifactSha256, digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Moderation evidence does not match the generated artifact.");
        ValidateCost(decision.Cost, decision.Currency, currency, "Moderation");
    }

    private static void ValidateClearance(
        ExternalRightsClearanceDecision decision,
        ImageProviderOutput output,
        string digest,
        string currency,
        string requiredTerritory)
    {
        if (string.IsNullOrWhiteSpace(decision.ClearanceId) || string.IsNullOrWhiteSpace(decision.Registry) ||
            string.IsNullOrWhiteSpace(decision.LicenseReference) || string.IsNullOrWhiteSpace(decision.RightsHolder))
            throw new InvalidOperationException("External rights-clearance evidence is incomplete.");
        if (!string.Equals(decision.ArtifactSha256, digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Rights-clearance evidence does not match the generated artifact.");
        if (!string.Equals(decision.LicenseReference, output.LicenseReference, StringComparison.Ordinal) ||
            !string.Equals(decision.RightsHolder, output.RightsHolder, StringComparison.Ordinal))
            throw new InvalidOperationException("External rights clearance contradicts provider evidence.");
        if (!string.Equals(decision.Territory, requiredTerritory, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(decision.Territory, "WORLDWIDE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("External rights clearance does not cover the required territory.");
        ValidateCost(decision.Cost, decision.Currency, currency, "Rights clearance");
    }

    private static void ValidateCost(decimal cost, string actualCurrency, string requiredCurrency, string source)
    {
        if (cost < 0 || !string.Equals(actualCurrency, requiredCurrency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(source + " cost evidence violates policy.");
    }

    private string ConfinedPath(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_workspaceRoot, relative));
        var prefix = _workspaceRoot.EndsWith(Path.DirectorySeparatorChar) ? _workspaceRoot : _workspaceRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Commercial verification path escapes the workspace.");
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

public sealed class DeterministicImageSafetyModerator : IImageSafetyModerator
{
    public ValueTask<ImageModerationDecision> ModerateAsync(ImageGenerationRequest request, ImageProviderOutput output, string artifactSha256, CancellationToken ct = default)
        => ValueTask.FromResult(new ImageModerationDecision(
            Approved: true,
            DecisionId: "moderation-" + request.AssetId.ToString("N"),
            PolicyVersion: "reference-safe-v1",
            ArtifactSha256: artifactSha256,
            Cost: 0m,
            Currency: request.Policy.Currency,
            CapturedAtUtc: DateTimeOffset.UnixEpoch));
}

public sealed class DeterministicExternalRightsClearanceService : IExternalRightsClearanceService
{
    public ValueTask<ExternalRightsClearanceDecision> VerifyAsync(ImageGenerationRequest request, ImageProviderOutput output, string artifactSha256, CancellationToken ct = default)
        => ValueTask.FromResult(new ExternalRightsClearanceDecision(
            Cleared: true,
            ClearanceId: "clearance-" + request.AssetId.ToString("N"),
            Registry: "reference-rights-registry",
            ArtifactSha256: artifactSha256,
            LicenseReference: output.LicenseReference,
            RightsHolder: output.RightsHolder,
            Territory: output.Territory,
            Cost: 0m,
            Currency: request.Policy.Currency,
            CapturedAtUtc: DateTimeOffset.UnixEpoch));
}
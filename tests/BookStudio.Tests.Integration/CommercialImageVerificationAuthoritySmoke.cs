using BookStudio.Application.Authoring;
using System.Text.Json;

namespace BookStudio.Tests.Integration;

internal static class CommercialImageVerificationAuthoritySmoke
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var root = Path.Combine(workspaceRoot, "vs125-commercial-image-verification");
        var assetId = Guid.NewGuid();
        var policy = new ImageGenerationPolicy(
            MaxCost: 1m,
            Currency: "USD",
            MaxRepairAttempts: 1,
            AllowedLicenseKinds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PROJECT_OWNED" },
            RequiredTerritory: "WORLDWIDE");
        var request = new ImageGenerationRequest(
            "workspace-vs125",
            assetId,
            "A restrained literary cover without text",
            "Abstract blue and gold cover composition",
            1200,
            1800,
            policy);

        var verifiedProvider = new CommercialImageVerificationAuthority(
            new DeterministicLicensedSvgProvider(),
            new DeterministicImageSafetyModerator(),
            new DeterministicExternalRightsClearanceService(),
            root);
        var pipeline = new ImageProviderRightsPipeline(verifiedProvider, root);

        var first = await pipeline.ExecuteAsync(request);
        Require(first.Provenance.Provider.StartsWith("verified:", StringComparison.Ordinal), "verified provider identity was not persisted");
        Require(first.Rights.LicenseReference == "license-" + assetId.ToString("N"), "external clearance was not propagated");

        var verificationPath = Path.Combine(root, "commercial-image-verification", assetId.ToString("N") + ".json");
        Require(File.Exists(verificationPath), "commercial verification evidence was not persisted");
        var originalEvidenceJson = await File.ReadAllTextAsync(verificationPath);

        var restartedProvider = new CommercialImageVerificationAuthority(
            new DeterministicLicensedSvgProvider(),
            new ThrowingModerator(),
            new ThrowingRightsClearance(),
            root);
        var directlyReused = await restartedProvider.GenerateAsync(request);
        Require(directlyReused.LicenseReference == first.Rights.LicenseReference, "verified authority restart did not reuse exact clearance evidence");

        var restartedPipeline = new ImageProviderRightsPipeline(restartedProvider, root);
        var restarted = await restartedPipeline.ExecuteAsync(request);
        Require(restarted.ReusedExistingArtifact, "restart did not reuse the exact verified artifact");
        Require(restarted.Sha256 == first.Sha256, "restart changed verified image bytes");

        var persisted = JsonSerializer.Deserialize<CommercialImageVerificationEvidence>(originalEvidenceJson)
            ?? throw new InvalidOperationException("commercial verification evidence could not be deserialized");
        await File.WriteAllTextAsync(
            verificationPath,
            JsonSerializer.Serialize(persisted with { Currency = "EUR" }, new JsonSerializerOptions { WriteIndented = true }));
        await ExpectProviderFailureAsync(
            new CommercialImageVerificationAuthority(
                new DeterministicLicensedSvgProvider(),
                new ThrowingModerator(),
                new ThrowingRightsClearance(),
                root),
            request,
            "tampered persisted verification evidence was reused");
        await File.WriteAllTextAsync(verificationPath, originalEvidenceJson);

        await ExpectFailureAsync(
            new ImageProviderRightsPipeline(
                new CommercialImageVerificationAuthority(
                    new DeterministicLicensedSvgProvider(),
                    new RejectingModerator(),
                    new DeterministicExternalRightsClearanceService(),
                    Path.Combine(root, "moderation-rejection")),
                Path.Combine(root, "moderation-rejection")),
            request with { AssetId = Guid.NewGuid() },
            "unsafe image was accepted");

        await ExpectFailureAsync(
            new ImageProviderRightsPipeline(
                new CommercialImageVerificationAuthority(
                    new DeterministicLicensedSvgProvider(),
                    new DeterministicImageSafetyModerator(),
                    new RejectingRightsClearance(),
                    Path.Combine(root, "rights-rejection")),
                Path.Combine(root, "rights-rejection")),
            request with { AssetId = Guid.NewGuid() },
            "uncleared image was accepted");

        await ExpectFailureAsync(
            new ImageProviderRightsPipeline(
                new CommercialImageVerificationAuthority(
                    new DeterministicLicensedSvgProvider(),
                    new CostlyModerator(),
                    new DeterministicExternalRightsClearanceService(),
                    Path.Combine(root, "cost-rejection")),
                Path.Combine(root, "cost-rejection")),
            request with { AssetId = Guid.NewGuid(), Policy = policy with { MaxCost = 0.25m } },
            "verification cost above policy was accepted");
    }

    private static async Task ExpectFailureAsync(ImageProviderRightsPipeline pipeline, ImageGenerationRequest request, string message)
    {
        try
        {
            await pipeline.ExecuteAsync(request);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static async Task ExpectProviderFailureAsync(IImageGenerationProvider provider, ImageGenerationRequest request, string message)
    {
        try
        {
            await provider.GenerateAsync(request);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ThrowingModerator : IImageSafetyModerator
    {
        public ValueTask<ImageModerationDecision> ModerateAsync(ImageGenerationRequest request, ImageProviderOutput output, string artifactSha256, CancellationToken ct = default)
            => throw new InvalidOperationException("moderator must not run during verified restart reuse");
    }

    private sealed class ThrowingRightsClearance : IExternalRightsClearanceService
    {
        public ValueTask<ExternalRightsClearanceDecision> VerifyAsync(ImageGenerationRequest request, ImageProviderOutput output, string artifactSha256, CancellationToken ct = default)
            => throw new InvalidOperationException("rights service must not run during verified restart reuse");
    }

    private sealed class RejectingModerator : IImageSafetyModerator
    {
        public ValueTask<ImageModerationDecision> ModerateAsync(ImageGenerationRequest request, ImageProviderOutput output, string artifactSha256, CancellationToken ct = default)
            => ValueTask.FromResult(new ImageModerationDecision(false, "reject", "safety-v1", artifactSha256, 0m, request.Policy.Currency, DateTimeOffset.UnixEpoch, "unsafe"));
    }

    private sealed class RejectingRightsClearance : IExternalRightsClearanceService
    {
        public ValueTask<ExternalRightsClearanceDecision> VerifyAsync(ImageGenerationRequest request, ImageProviderOutput output, string artifactSha256, CancellationToken ct = default)
            => ValueTask.FromResult(new ExternalRightsClearanceDecision(false, "reject", "registry", artifactSha256, output.LicenseReference, output.RightsHolder, output.Territory, 0m, request.Policy.Currency, DateTimeOffset.UnixEpoch, "not cleared"));
    }

    private sealed class CostlyModerator : IImageSafetyModerator
    {
        public ValueTask<ImageModerationDecision> ModerateAsync(ImageGenerationRequest request, ImageProviderOutput output, string artifactSha256, CancellationToken ct = default)
            => ValueTask.FromResult(new ImageModerationDecision(true, "costly", "safety-v1", artifactSha256, 0.50m, request.Policy.Currency, DateTimeOffset.UnixEpoch));
    }
}

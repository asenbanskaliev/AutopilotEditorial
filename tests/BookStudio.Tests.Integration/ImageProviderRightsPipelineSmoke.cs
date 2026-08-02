using BookStudio.Application.Authoring;

namespace BookStudio.Tests.Integration;

internal static class ImageProviderRightsPipelineSmoke
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var root = Path.Combine(workspaceRoot, "vs124-images");
        var provider = new DeterministicLicensedSvgProvider();
        var pipeline = new ImageProviderRightsPipeline(provider, root);
        var assetId = Guid.Parse("12400000-0000-0000-0000-000000000001");
        var policy = new ImageGenerationPolicy(
            1m,
            "USD",
            1,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PROJECT_OWNED" },
            "WORLDWIDE");
        var request = new ImageGenerationRequest(
            "workspace-vs124",
            assetId,
            "A professional editorial cover with a lighthouse at dawn",
            "A lighthouse at dawn on a professional book cover",
            1600,
            2560,
            policy);

        var first = await pipeline.ExecuteAsync(request);
        Assert(first.ByteSize > 0, "Image bytes must be persisted.");
        Assert(first.MediaType == "image/svg+xml", "Media type must be exact.");
        Assert(first.Rights.LicenseKind == "PROJECT_OWNED", "Rights policy must be recorded.");
        Assert(first.Rights.Territory == "WORLDWIDE", "Rights territory must be recorded.");
        Assert(!string.IsNullOrWhiteSpace(first.Provenance.EvidenceDigest), "Provenance digest is required.");
        Assert(!string.IsNullOrWhiteSpace(first.Accessibility.AltText), "Alt text is required.");
        Assert(first.RepairAttempts == 0, "Reference provider should not consume repair attempts.");

        var second = await new ImageProviderRightsPipeline(provider, root).ExecuteAsync(request);
        Assert(second.ReusedExistingArtifact, "Restart must reuse exact verified bytes.");
        Assert(second.Sha256 == first.Sha256, "Restart replay must be byte-idempotent.");

        await ExpectFailureAsync(() => new ImageProviderRightsPipeline(
            new InvalidRightsProvider(),
            Path.Combine(root, "invalid-rights")).ExecuteAsync(request));

        await ExpectFailureAsync(() => new ImageProviderRightsPipeline(
            new ExcessiveCostProvider(),
            Path.Combine(root, "invalid-cost")).ExecuteAsync(request));

        var boundedProvider = new AlwaysFailingProvider();
        await ExpectFailureAsync(() => new ImageProviderRightsPipeline(
            boundedProvider,
            Path.Combine(root, "bounded-repair")).ExecuteAsync(request));
        Assert(boundedProvider.GenerationCalls == 2, "Repair attempts must stop at the configured ceiling.");
    }

    private static async Task ExpectFailureAsync(Func<ValueTask<ImageArtifactEvidence>> action)
    {
        try
        {
            await action();
            throw new InvalidOperationException("Expected fail-closed image pipeline rejection.");
        }
        catch (InvalidOperationException ex) when (ex.Message != "Expected fail-closed image pipeline rejection.")
        {
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class InvalidRightsProvider : IImageGenerationProvider
    {
        public string ProviderId => "invalid-rights";
        public ValueTask<ImageProviderQuote> QuoteAsync(ImageGenerationRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(new ImageProviderQuote(0m, "USD"));
        public ValueTask<ImageProviderOutput> GenerateAsync(ImageGenerationRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(new ImageProviderOutput(
                "<svg/>"u8.ToArray(), "image/svg+xml", "invalid", "request", 0m, "USD",
                "UNKNOWN", "", "", "ES", DateTimeOffset.UnixEpoch));
    }

    private sealed class ExcessiveCostProvider : IImageGenerationProvider
    {
        public string ProviderId => "expensive";
        public ValueTask<ImageProviderQuote> QuoteAsync(ImageGenerationRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(new ImageProviderQuote(request.Policy.MaxCost + 1m, request.Policy.Currency));
        public ValueTask<ImageProviderOutput> GenerateAsync(ImageGenerationRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class AlwaysFailingProvider : IImageGenerationProvider
    {
        public int GenerationCalls { get; private set; }
        public string ProviderId => "always-failing";
        public ValueTask<ImageProviderQuote> QuoteAsync(ImageGenerationRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(new ImageProviderQuote(0m, request.Policy.Currency));
        public ValueTask<ImageProviderOutput> GenerateAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            GenerationCalls++;
            throw new InvalidOperationException("transient provider failure");
        }
    }
}
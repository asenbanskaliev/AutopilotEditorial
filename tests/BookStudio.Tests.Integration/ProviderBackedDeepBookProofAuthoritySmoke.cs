using BookStudio.Application.Authoring;

namespace BookStudio.Tests.Integration;

internal static class ProviderBackedDeepBookProofAuthoritySmoke
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var root = Path.Combine(workspaceRoot, "vs124-authority");
        var storeRoot = Path.Combine(root, "store");
        var artifactRoot = Path.Combine(root, "artifacts");
        var request = new DeepBookProofRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "workspace-vs124-authority",
            "A literary mystery about a clockmaker whose map keeps moving after midnight.",
            new DeepBookProofPolicy(
                5m,
                "EUR",
                2,
                new HashSet<string>(["EPUB", "PDF", "DOCX", "KDP"], StringComparer.OrdinalIgnoreCase),
                true),
            "autopilot-editorial");
        var journey = CompletedJourney(request);
        var provider = new LocalDeterministicPublicationProvider();
        var pipeline = new PublicationArtifactPipeline([provider]);
        var imageProvider = new DeterministicLicensedSvgProvider();
        var imagePipeline = new ImageProviderRightsPipeline(imageProvider, artifactRoot);
        var imageRequest = new ImageGenerationRequest(
            request.WorkspaceId,
            Guid.NewGuid(),
            "A moonlit antique clockmaker workshop with a living map",
            "Moonlit clockmaker workshop with a map shifting across the table",
            1600,
            2560,
            new ImageGenerationPolicy(1m, "EUR", 1, new HashSet<string>(["PROJECT_OWNED"]), "WORLDWIDE"));
        var firstAuthority = new ProviderBackedDeepBookProofAuthority(
            new DeepBookProofCoordinator(new FileDeepBookProofStore(storeRoot), artifactRoot),
            pipeline,
            provider.ProviderId,
            artifactRoot,
            imagePipeline);

        var first = await firstAuthority.ExecuteAsync(
            request,
            journey,
            "The Clockmaker's Map",
            "Autopilot Editorial",
            "en",
            "Chapter One\nThe clock stopped at midnight, but the map continued to move.",
            DateTimeOffset.UtcNow,
            imageRequest);

        Require(first.ReadyForPublication, "natural-language proof must reach publication readiness without technical commands");
        Require(first.Publication is not null, "the real publication provider must execute inside the deep proof authority");
        Require(first.Image is not null, "the image authority must execute inside the durable proof authority");
        Require(!first.Publication!.ReusedExistingArtifacts, "the first provider execution must not claim reuse");
        Require(first.Checkpoint.Artifacts.Count == 4 && first.Checkpoint.Artifacts.All(x => x.Verified),
            "the final checkpoint must register and verify all provider artifacts");
        Require(first.Checkpoint.ImageArtifacts is { Count: 1 },
            "the final checkpoint must persist image rights, provenance and accessibility evidence");
        Require(first.Image!.Provenance.ProviderId == imageProvider.ProviderId &&
                !string.IsNullOrWhiteSpace(first.Image.Rights.LicenseReference) &&
                !string.IsNullOrWhiteSpace(first.Image.Accessibility.AltText),
            "image evidence must remain complete and fail-closed");

        var restartedAuthority = new ProviderBackedDeepBookProofAuthority(
            new DeepBookProofCoordinator(new FileDeepBookProofStore(storeRoot), artifactRoot),
            pipeline,
            provider.ProviderId,
            artifactRoot,
            new ImageProviderRightsPipeline(imageProvider, artifactRoot));
        var replay = await restartedAuthority.ExecuteAsync(
            request,
            journey,
            "The Clockmaker's Map",
            "Autopilot Editorial",
            "en",
            "Chapter One\nThe clock stopped at midnight, but the map continued to move.",
            DateTimeOffset.UtcNow,
            imageRequest);

        Require(replay.ReadyForPublication, "restart must recover the terminal provider-backed checkpoint");
        Require(replay.Checkpoint.Revision == first.Checkpoint.Revision,
            "terminal restart must not duplicate checkpoint effects");
        Require(replay.Checkpoint.Artifacts.Select(x => x.Sha256).SequenceEqual(first.Checkpoint.Artifacts.Select(x => x.Sha256)),
            "restart must preserve exact publication bytes and evidence");
        Require(replay.Checkpoint.ImageArtifacts!.Single().Sha256 == first.Checkpoint.ImageArtifacts!.Single().Sha256,
            "restart must preserve exact image bytes, rights and provenance evidence");

        var rejected = request with { ProofId = Guid.NewGuid() };
        var rejectedJourney = CompletedJourney(rejected);
        var rejectedAuthority = new ProviderBackedDeepBookProofAuthority(
            new DeepBookProofCoordinator(new FileDeepBookProofStore(Path.Combine(root, "rejected-store")), artifactRoot),
            pipeline,
            provider.ProviderId,
            artifactRoot);
        var failedClosed = false;
        try
        {
            await rejectedAuthority.ExecuteAsync(rejected, rejectedJourney, "Rejected", "Autopilot Editorial", "en", "text", DateTimeOffset.UtcNow);
        }
        catch (DeepBookProofValidationException)
        {
            failedClosed = true;
        }
        Require(failedClosed, "publication must fail closed when required image authority evidence is absent");
    }

    private static BookCreationJourney CompletedJourney(DeepBookProofRequest request)
    {
        var phases = Enum.GetValues<JourneyPhase>();
        var now = DateTimeOffset.UtcNow;
        var progress = phases.Select(x => new JourneyPhaseProgress(
            x,
            JourneyPhaseStatus.Approved,
            1,
            1,
            "Approved",
            new JourneyAuthorityReference(x, "integration", Guid.NewGuid(), 1, "digest-" + x, true, true),
            now,
            now)).ToArray();
        return new BookCreationJourney(
            request.JourneyId,
            Guid.NewGuid(),
            request.WorkspaceId,
            new BookCreationBrief(
                request.NaturalLanguageIdea,
                "adult",
                "mystery",
                "en",
                80000,
                "literary",
                false,
                new HashSet<string>(["EPUB", "PDF", "DOCX", "KDP"]),
                5m,
                "EUR",
                "en"),
            new JourneyAutonomyPolicy(
                JourneyAutonomyMode.Autonomous,
                2,
                10m,
                false,
                false,
                false,
                false,
                new HashSet<JourneyDecisionKind>()),
            JourneyStatus.Completed,
            JourneyPhase.ReleaseReady,
            progress,
            Array.Empty<JourneyDecision>(),
            Array.Empty<JourneyRepairState>(),
            new JourneyNextAction(JourneyActionKind.None, JourneyPhase.ReleaseReady, "Complete", false, "terminal"),
            10,
            null,
            now,
            now);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

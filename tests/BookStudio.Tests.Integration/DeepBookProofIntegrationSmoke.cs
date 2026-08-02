using System.Security.Cryptography;
using BookStudio.Application.Authoring;

namespace BookStudio.Tests.Integration;

internal static class DeepBookProofIntegrationSmoke
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var storeRoot = Path.Combine(workspaceRoot, "deep-proof-store");
        var artifactRoot = Path.Combine(workspaceRoot, "deep-proof-artifacts");
        Directory.CreateDirectory(artifactRoot);

        var request = new DeepBookProofRequest(
            Guid.NewGuid(), Guid.NewGuid(), "workspace-vs122",
            "A professional mystery novel about memory and responsibility.",
            new DeepBookProofPolicy(50m, "EUR", 2,
                new HashSet<string>(["EPUB", "PDF", "DOCX", "KDP"], StringComparer.OrdinalIgnoreCase)),
            "integration-smoke");

        var journey = CompletedJourney(request);
        var firstCoordinator = new DeepBookProofCoordinator(new FileDeepBookProofStore(storeRoot), artifactRoot);

        var created = await firstCoordinator.StartOrResumeAsync(request, journey, 0m, null, DateTimeOffset.UtcNow);
        Require(created.Checkpoint.Phase == DeepBookProofPhase.Intake && created.Checkpoint.Revision == 1,
            "Proof must start at one durable intake checkpoint.");

        var intake = await firstCoordinator.StartOrResumeAsync(request, journey, 1m, null, DateTimeOffset.UtcNow);
        Require(intake.Checkpoint.Phase == DeepBookProofPhase.JourneyExecution, "Intake must continue without commands.");

        // Simulate process interruption by discarding the coordinator and rebuilding it over the same durable store.
        var resumedCoordinator = new DeepBookProofCoordinator(new FileDeepBookProofStore(storeRoot), artifactRoot);
        var journeyDone = await resumedCoordinator.StartOrResumeAsync(request, journey, 1m, null, DateTimeOffset.UtcNow);
        Require(journeyDone.Checkpoint.Phase == DeepBookProofPhase.ArtifactProduction,
            "Restart must resume from the last committed checkpoint.");

        var artifacts = new List<DeepBookArtifact>();
        foreach (var format in new[] { "EPUB", "PDF", "DOCX", "KDP" })
        {
            var relative = Path.Combine(request.ProofId.ToString("N"), format.ToLowerInvariant() + (format == "KDP" ? ".zip" : ".bin"));
            var full = Path.Combine(artifactRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var payload = System.Text.Encoding.UTF8.GetBytes($"VS-122 {format} exact package artifact");
            await File.WriteAllBytesAsync(full, payload);
            artifacts.Add(new DeepBookArtifact(format, relative, MediaType(format), payload.LongLength,
                Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), "VS-122 integration proof", false));
        }

        var produced = await resumedCoordinator.StartOrResumeAsync(request, journey, 2m, artifacts, DateTimeOffset.UtcNow);
        Require(produced.Checkpoint.Phase == DeepBookProofPhase.ArtifactVerification,
            "Produced artifacts must enter exact verification.");

        var ready = await resumedCoordinator.StartOrResumeAsync(request, journey, 1m, null, DateTimeOffset.UtcNow);
        Require(ready.ReadyForPublication && ready.Checkpoint.Status == DeepBookProofStatus.Ready,
            "All exact required artifacts must freeze publication readiness.");
        Require(ready.Checkpoint.Artifacts.All(x => x.Verified), "Every final artifact must be digest verified.");

        var replay = await resumedCoordinator.StartOrResumeAsync(request, journey, 0m, null, DateTimeOffset.UtcNow);
        Require(replay.Replayed && replay.Checkpoint.Revision == ready.Checkpoint.Revision,
            "Terminal replay must be idempotent and must not advance revision.");

        var boundedRequest = request with { ProofId = Guid.NewGuid(), Policy = request.Policy with { MaximumRepairAttempts = 1 } };
        var boundedJourney = journey with { JourneyId = boundedRequest.JourneyId };
        var bounded = new DeepBookProofCoordinator(new FileDeepBookProofStore(storeRoot), artifactRoot);
        await bounded.StartOrResumeAsync(boundedRequest, boundedJourney, 0m, null, DateTimeOffset.UtcNow);
        await bounded.RecordRepairAsync(boundedRequest, "first repair", 1m, DateTimeOffset.UtcNow);
        var exhausted = await bounded.RecordRepairAsync(boundedRequest, "repair budget exhausted", 1m, DateTimeOffset.UtcNow);
        Require(exhausted.Status == DeepBookProofStatus.WaitingForDecision,
            "Exhausted repair budget must fail closed into a human decision.");
    }

    private static BookCreationJourney CompletedJourney(DeepBookProofRequest request)
    {
        var phases = Enum.GetValues<JourneyPhase>();
        var now = DateTimeOffset.UtcNow;
        var progress = phases.Select(x => new JourneyPhaseProgress(x, JourneyPhaseStatus.Approved, 1, 1,
            "Approved", new JourneyAuthorityReference(x, "integration", Guid.NewGuid(), 1, "digest-" + x, true, true), now, now)).ToArray();
        return new BookCreationJourney(request.JourneyId, Guid.NewGuid(), request.WorkspaceId,
            new BookCreationBrief(request.NaturalLanguageIdea, "adult", "mystery", "en", 80000, "literary", false,
                new HashSet<string>(["EPUB", "PDF", "DOCX", "KDP"]), 50m, "EUR", "en"),
            new JourneyAutonomyPolicy(JourneyAutonomyMode.Autonomous, 2, 10m, false, false, false, false,
                new HashSet<JourneyDecisionKind>()),
            JourneyStatus.Completed, JourneyPhase.ReleaseReady, progress, Array.Empty<JourneyDecision>(),
            Array.Empty<JourneyRepairState>(),
            new JourneyNextAction(JourneyActionKind.None, JourneyPhase.ReleaseReady, "Complete", false, "terminal"),
            10, null, now, now);
    }

    private static string MediaType(string format) => format switch
    {
        "EPUB" => "application/epub+zip",
        "PDF" => "application/pdf",
        "DOCX" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "KDP" => "application/zip",
        _ => "application/octet-stream"
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Application.Authoring;

namespace BookStudio.Tests.Integration;

internal static class HumanCentricBookCreationAuditSmoke
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var root = Path.Combine(workspaceRoot, "vs127-human-audit");
        var storeRoot = Path.Combine(root, "store");
        var artifactRoot = Path.Combine(root, "artifacts");
        var evidenceRoot = Path.Combine(root, "evidence");
        Directory.CreateDirectory(evidenceRoot);

        var idea = "A hopeful literary mystery about a retired cartographer whose final atlas redraws one forgotten village each night.";
        var request = new DeepBookProofRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "workspace-vs127-human-audit",
            idea,
            new DeepBookProofPolicy(
                6m,
                "EUR",
                2,
                new HashSet<string>(["EPUB", "PDF", "DOCX", "KDP"], StringComparer.OrdinalIgnoreCase),
                true),
            "autopilot-editorial");
        var journey = CompletedJourney(request);
        var manuscript = "Chapter One\nThe atlas had been blank for thirty years. On the night Mara decided to burn it, a road appeared where no road had ever been.";

        var publicationProvider = new LocalDeterministicPublicationProvider();
        var publicationPipeline = new PublicationArtifactPipeline([publicationProvider]);
        var imageProvider = new DeterministicLicensedSvgProvider();
        var imageRequest = new ImageGenerationRequest(
            request.WorkspaceId,
            Guid.NewGuid(),
            "An old atlas glowing on a cartographer's desk while a forgotten village appears on the page",
            "A glowing atlas on a cartographer's desk revealing a forgotten village",
            1600,
            2560,
            new ImageGenerationPolicy(1m, "EUR", 1, new HashSet<string>(["PROJECT_OWNED"]), "WORLDWIDE"));

        var authority = CreateAuthority(storeRoot, artifactRoot, publicationPipeline, publicationProvider, imageProvider);
        var first = await authority.ExecuteAsync(
            request,
            journey,
            "The Atlas of Forgotten Roads",
            "Autopilot Editorial",
            "en",
            manuscript,
            DateTimeOffset.UtcNow,
            imageRequest);

        Require(first.ReadyForPublication, "a non-technical natural-language journey must reach publication readiness");
        Require(first.Checkpoint.Status == DeepBookProofStatus.Ready, "the durable checkpoint must be terminal-ready");
        Require(first.Publication is not null && !first.Publication.ReusedExistingArtifacts,
            "the first run must execute the real publication provider exactly once");
        Require(first.Checkpoint.Artifacts.Count == 4, "EPUB, PDF, DOCX and KDP artifacts are required");
        Require(first.Checkpoint.Artifacts.All(x => x.Verified && !string.IsNullOrWhiteSpace(x.Sha256)),
            "all publication artifacts must preserve exact verified digests");
        Require(first.Image is not null && first.Image.Accessibility.AltText.Length > 0,
            "the generated image must preserve accessibility evidence");
        Require(!string.IsNullOrWhiteSpace(first.Image!.Rights.LicenseReference) &&
                first.Image.Rights.Territory.Equals("WORLDWIDE", StringComparison.OrdinalIgnoreCase),
            "commercial rights evidence must be complete");
        Require(first.Checkpoint.AccumulatedCost <= request.Policy.MaximumCost,
            "the full journey must remain within the declared book budget");

        var checkpointRevision = first.Checkpoint.Revision;
        var firstCost = first.Checkpoint.AccumulatedCost;
        var artifactDigests = first.Checkpoint.Artifacts.Select(x => x.Sha256).ToArray();
        var imageDigest = first.Image.Sha256;

        var restartedAuthority = CreateAuthority(storeRoot, artifactRoot, publicationPipeline, publicationProvider, imageProvider);
        var replay = await restartedAuthority.ExecuteAsync(
            request,
            journey,
            "The Atlas of Forgotten Roads",
            "Autopilot Editorial",
            "en",
            manuscript,
            DateTimeOffset.UtcNow,
            imageRequest);

        Require(replay.ReadyForPublication, "restart must recover the completed no-command journey");
        Require(replay.Checkpoint.Revision == checkpointRevision, "restart must not repeat terminal phases");
        Require(replay.Checkpoint.AccumulatedCost == firstCost, "restart must not duplicate provider cost");
        Require(replay.Checkpoint.Artifacts.Select(x => x.Sha256).SequenceEqual(artifactDigests),
            "restart must preserve exact publication bytes");
        Require(replay.Image?.Sha256 == imageDigest, "restart must preserve exact image rights evidence");

        var rejectedRequest = request with { ProofId = Guid.NewGuid(), JourneyId = Guid.NewGuid() };
        var rejected = false;
        try
        {
            var rejectedAuthority = new ProviderBackedDeepBookProofAuthority(
                new DeepBookProofCoordinator(new FileDeepBookProofStore(Path.Combine(root, "rejected-store")), artifactRoot),
                publicationPipeline,
                publicationProvider.ProviderId,
                artifactRoot);
            await rejectedAuthority.ExecuteAsync(
                rejectedRequest,
                CompletedJourney(rejectedRequest),
                "Unsafe release",
                "Autopilot Editorial",
                "en",
                manuscript,
                DateTimeOffset.UtcNow);
        }
        catch (DeepBookProofValidationException)
        {
            rejected = true;
        }
        Require(rejected, "publication must fail closed when required image rights evidence is unavailable");

        var evidence = new
        {
            audit = "VS-127",
            naturalLanguageIdeaSha256 = Sha256(idea),
            request.ProofId,
            request.JourneyId,
            request.WorkspaceId,
            status = first.Checkpoint.Status.ToString(),
            readyForPublication = first.ReadyForPublication,
            restartRecovered = replay.ReadyForPublication,
            duplicateCostAfterRestart = replay.Checkpoint.AccumulatedCost - firstCost,
            repairCeiling = request.Policy.MaximumRepairAttempts,
            maximumCost = request.Policy.MaximumCost,
            currency = request.Policy.CostCurrency,
            accumulatedCost = firstCost,
            publicationArtifacts = first.Checkpoint.Artifacts.Select(x => new { x.Format, x.Sha256, x.ByteSize, x.Verified }),
            image = new
            {
                first.Image.Sha256,
                first.Image.Provenance.Provider,
                first.Image.Provenance.Model,
                first.Image.Rights.LicenseReference,
                first.Image.Rights.Territory,
                first.Image.Accessibility.AltText,
            },
            adversarialMissingRightsRejected = rejected,
        };
        var evidencePath = Path.Combine(evidenceRoot, "human-centric-book-audit.json");
        var temporary = evidencePath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, evidencePath, true);
        Require(File.Exists(evidencePath), "the audit must persist exact evidence atomically");
    }

    private static ProviderBackedDeepBookProofAuthority CreateAuthority(
        string storeRoot,
        string artifactRoot,
        PublicationArtifactPipeline publicationPipeline,
        LocalDeterministicPublicationProvider publicationProvider,
        DeterministicLicensedSvgProvider imageProvider)
    {
        return new ProviderBackedDeepBookProofAuthority(
            new DeepBookProofCoordinator(new FileDeepBookProofStore(storeRoot), artifactRoot),
            publicationPipeline,
            publicationProvider.ProviderId,
            artifactRoot,
            new ImageProviderRightsPipeline(imageProvider, artifactRoot));
    }

    private static BookCreationJourney CompletedJourney(DeepBookProofRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var progress = Enum.GetValues<JourneyPhase>().Select(phase => new JourneyPhaseProgress(
            phase,
            JourneyPhaseStatus.Approved,
            1,
            1,
            "Approved by executable human-centric audit",
            new JourneyAuthorityReference(phase, "vs127-audit", Guid.NewGuid(), 1, "digest-" + phase, true, true),
            now,
            now)).ToArray();
        return new BookCreationJourney(
            request.JourneyId,
            Guid.NewGuid(),
            request.WorkspaceId,
            new BookCreationBrief(
                request.NaturalLanguageIdea,
                "adult",
                "literary mystery",
                "en",
                80000,
                "hopeful and atmospheric",
                false,
                new HashSet<string>(["EPUB", "PDF", "DOCX", "KDP"]),
                request.Policy.MaximumCost,
                request.Policy.CostCurrency,
                "en"),
            new JourneyAutonomyPolicy(
                JourneyAutonomyMode.Autonomous,
                request.Policy.MaximumRepairAttempts,
                request.Policy.MaximumCost,
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

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

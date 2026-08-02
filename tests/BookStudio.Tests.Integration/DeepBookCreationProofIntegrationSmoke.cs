using System.Runtime.CompilerServices;
using BookStudio.Application.Authoring;

internal static class DeepBookCreationProofIntegrationSmoke
{
    [ModuleInitializer]
    internal static void Run()
    {
        var now = DateTimeOffset.Parse("2026-08-02T01:20:00Z");
        var journeyStore = new InMemoryBookCreationJourneyStore();
        var draft = new BookCreationJourneyDraft(
            Guid.NewGuid(), Guid.NewGuid(), "vs122-workspace",
            new BookCreationBrief(
                "Write a professional mystery novel with a complete publication package.",
                "Adult mystery readers", "mystery", "es-ES", 70000, "tense and literary", true,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EPUB", "PDF", "DOCX", "KDP" },
                100m, "EUR", "es-ES"),
            new JourneyAutonomyPolicy(JourneyAutonomyMode.Autonomous, 2, 20m, false, false, false, false,
                new HashSet<JourneyDecisionKind> { JourneyDecisionKind.LegalRisk, JourneyDecisionKind.SafetyRisk }),
            "integration-test", "vs122-create");
        var journey = journeyStore.CreateAsync(draft, now).AsTask().GetAwaiter().GetResult().Journey;

        var checkpoints = new InMemoryDeepBookCreationCheckpointStore();
        var executor = new InterruptOnceExecutor();
        var orchestrator = new DeepBookCreationProofOrchestrator(checkpoints, executor);

        var interrupted = false;
        try
        {
            orchestrator.RunAsync(journey, now).AsTask().GetAwaiter().GetResult();
        }
        catch (SimulatedRestartException)
        {
            interrupted = true;
        }
        Require(interrupted, "The interruption scenario did not occur.");

        var persisted = checkpoints.GetAsync(journey.WorkspaceId, journey.JourneyId).AsTask().GetAwaiter().GetResult();
        Require(persisted is not null && persisted.Authorities.Count == 3, "Committed phases were not persisted before restart.");

        var resumed = new DeepBookCreationProofOrchestrator(checkpoints, executor)
            .RunAsync(journey, now.AddMinutes(5)).AsTask().GetAwaiter().GetResult();
        Require(resumed.Resumed, "Execution did not report checkpoint recovery.");
        Require(resumed.Completed, "Deep proof did not complete.");
        Require(resumed.Checkpoint.Authorities.Count == Enum.GetValues<JourneyPhase>().Length,
            "Not every canonical phase produced exact authority evidence.");
        Require(resumed.FinalArtifacts.Select(x => x.Format).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(new[] { "EPUB", "PDF", "DOCX", "KDP" }), "Final package formats are incomplete.");
        Require(resumed.FinalArtifacts.All(x => x.PolicyApproved && x.SizeBytes > 0 && x.Sha256.Length == 64),
            "Final artifacts lack policy or integrity evidence.");
        Require(resumed.Checkpoint.CostSpent <= draft.Brief.MaximumCost, "Cost ceiling was exceeded.");
        Require(!string.IsNullOrWhiteSpace(resumed.PackageDigest), "Package digest was not frozen.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("VS-122 integration smoke failed: " + message);
    }

    private sealed class SimulatedRestartException : Exception { }

    private sealed class InterruptOnceExecutor : IDeepBookCreationPhaseExecutor
    {
        private bool _interrupted;

        public ValueTask<DeepBookCreationPhaseResult> ExecuteAsync(DeepBookCreationPhaseRequest request, CancellationToken ct = default)
        {
            if (!_interrupted && request.Phase == JourneyPhase.Authoring)
            {
                _interrupted = true;
                throw new SimulatedRestartException();
            }

            var artifacts = request.Phase == JourneyPhase.ReleaseReady
                ? new[]
                {
                    Artifact("EPUB", "application/epub+zip", "book.epub", 'a'),
                    Artifact("PDF", "application/pdf", "book-print.pdf", 'b'),
                    Artifact("DOCX", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "book.docx", 'c'),
                    Artifact("KDP", "application/zip", "kdp-package.zip", 'd')
                }
                : Array.Empty<DeepBookCreationArtifact>();

            return ValueTask.FromResult(new DeepBookCreationPhaseResult(
                request.Phase, true, false, 2m, request.Phase + "Authority", Guid.NewGuid(), 1,
                new string(((char)('0' + ((int)request.Phase % 10))), 64), artifacts, null));
        }

        private static DeepBookCreationArtifact Artifact(string format, string mediaType, string fileName, char digest) =>
            new(format, mediaType, fileName, 1024, new string(digest, 64), "integration-proof:deterministic-fixture", true);
    }
}

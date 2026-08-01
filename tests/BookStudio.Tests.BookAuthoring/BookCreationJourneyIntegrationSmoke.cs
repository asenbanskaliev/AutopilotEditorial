using System.Runtime.CompilerServices;
using BookStudio.Application.Authoring;

internal static class BookCreationJourneyIntegrationSmoke
{
    [ModuleInitializer]
    internal static void Run()
    {
        var store = new InMemoryBookCreationJourneyStore();
        var now = DateTimeOffset.Parse("2026-08-02T00:00:00Z");
        var draft = new BookCreationJourneyDraft(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "integration-workspace",
            new BookCreationBrief(
                "Write a professional mystery novel set in Pamplona.",
                "Adult mystery readers",
                "mystery",
                "es-ES",
                70000,
                "tense and literary",
                true,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EPUB", "PDF", "DOCX", "KDP" },
                50m,
                "EUR",
                "es-ES"),
            new JourneyAutonomyPolicy(
                JourneyAutonomyMode.Autonomous,
                3,
                10m,
                false,
                false,
                false,
                false,
                new HashSet<JourneyDecisionKind> { JourneyDecisionKind.LegalRisk, JourneyDecisionKind.SafetyRisk }),
            "integration-test",
            "create-001");

        var created = store.CreateAsync(draft, now).AsTask().GetAwaiter().GetResult();
        Require(!created.Replayed, "First journey creation unexpectedly replayed.");
        Require(created.Journey.NextAction.Kind == JourneyActionKind.StartPhase, "Journey did not start automatically.");
        Require(created.Journey.NextAction.Phase == JourneyPhase.Intake, "Journey did not start at intake.");

        var replay = store.CreateAsync(draft, now.AddSeconds(1)).AsTask().GetAwaiter().GetResult();
        Require(replay.Replayed && replay.Journey.JourneyId == created.Journey.JourneyId, "Create replay is not idempotent.");

        var journey = created.Journey;
        foreach (var phase in Enum.GetValues<JourneyPhase>())
        {
            var command = new BookCreationJourneyCommand(
                Guid.NewGuid(),
                journey.JourneyId,
                journey.WorkspaceId,
                journey.Revision,
                JourneyCommandKind.RecordAuthority,
                new JourneyAuthorityReference(
                    phase,
                    phase + "Authority",
                    Guid.NewGuid(),
                    1,
                    "digest-" + phase,
                    true,
                    true),
                null,
                null,
                null,
                "integration-test",
                "authority-" + phase);
            journey = store.ApplyAsync(command, now.AddMinutes((int)phase + 1)).AsTask().GetAwaiter().GetResult();
        }

        Require(journey.Status == JourneyStatus.Completed, "Journey did not complete after all approved authorities.");
        Require(journey.Progress.All(x => x.Status == JourneyPhaseStatus.Approved), "Not all phases were approved.");
        Require(journey.NextAction.Kind == JourneyActionKind.None, "Completed journey exposed a further executable action.");

        var restored = store.GetAsync(journey.WorkspaceId, journey.JourneyId).AsTask().GetAwaiter().GetResult();
        Require(restored is not null && restored.Revision == journey.Revision, "Journey checkpoint could not be restored.");
        Require(restored.Brief.OutputFormats.SetEquals(new[] { "EPUB", "PDF", "DOCX", "KDP" }), "Final output intent was not preserved.");

        var staleRejected = false;
        try
        {
            store.ApplyAsync(new BookCreationJourneyCommand(
                Guid.NewGuid(), journey.JourneyId, journey.WorkspaceId, 1,
                JourneyCommandKind.Pause, null, null, null, null,
                "integration-test", "stale"), now.AddHours(1)).AsTask().GetAwaiter().GetResult();
        }
        catch (BookCreationJourneyConflictException)
        {
            staleRejected = true;
        }
        Require(staleRejected, "Stale revision was not rejected.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("VS-121 integration smoke failed: " + message);
    }
}
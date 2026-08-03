using BookStudio.Autopilot.EditorialJourney;

await VerifySqliteCheckpointRestartAsync();
await VerifyWriterAndReviewerAdaptersAsync();
VerifyNaturalLanguageDefaults();
Console.WriteLine("VS-130 production editorial adapter tests PASS");

static async Task VerifySqliteCheckpointRestartAsync()
{
    var database = Path.Combine(Path.GetTempPath(), $"vs130-{Guid.NewGuid():N}.db");
    try
    {
        var connectionString = $"Data Source={database};Mode=ReadWriteCreate";
        var checkpoint = EditorialJourneyCheckpoint.New("vs130-test", "fingerprint") with
        {
            NextStage = EditorialJourneyStage.Outline,
            Events =
            [
                new EditorialJourneyEvent(
                    EditorialJourneyStage.Briefing,
                    "PASS",
                    "artifact_verified",
                    DateTimeOffset.Parse("2026-08-03T09:00:00Z")),
            ],
        };

        await using (var first = new SqliteEditorialJourneyCheckpointStore(connectionString))
        {
            await first.SaveAsync(checkpoint, CancellationToken.None);
        }

        await using (var restarted = new SqliteEditorialJourneyCheckpointStore(connectionString))
        {
            var loaded = await restarted.LoadAsync("vs130-test", CancellationToken.None);
            Require(loaded is not null, "Checkpoint was not restored after SQLite restart.");
            Require(loaded!.NextStage == EditorialJourneyStage.Outline, "Checkpoint stage changed after restart.");
            Require(loaded.Events.Count == 1, "Checkpoint evidence was not restored.");
        }
    }
    finally
    {
        File.Delete(database);
    }
}

static async Task VerifyWriterAndReviewerAdaptersAsync()
{
    var options = EditorialJourneyProductionOptions.CreateDefault(Path.GetTempPath(), "opencode");
    var invoker = new ScriptedModelInvoker();
    var generator = new OpenCodeEditorialContentGenerator(invoker, options);
    var reviewer = new OpenCodeIndependentEditorialReviewer(invoker, options);
    var request = new EditorialJourneyRequest(
        "vs130-test",
        "Una archivista descubre registros municipales borrados que anticipan desapariciones.",
        "es-ES",
        "Los registros borrados",
        1);

    var briefing = await generator.GenerateBriefingAsync(request, CancellationToken.None);
    Require(briefing.Model == "writer-free", "Writer model metadata was not retained.");
    Require(briefing.Markdown.Contains("Briefing", StringComparison.Ordinal), "Briefing adapter returned unexpected content.");

    var persistedBriefing = Artifact("vs130-test.draft.briefing", briefing.Markdown);
    var outline = await generator.GenerateOutlineAsync(request, persistedBriefing, CancellationToken.None);
    var persistedOutline = Artifact("vs130-test.draft.outline", outline.Markdown);
    var chapter = await generator.GenerateChapterAsync(request, persistedBriefing, persistedOutline, CancellationToken.None);
    var persistedChapter = Artifact("vs130-test.draft.chapter-01", chapter.Markdown);

    var review = await reviewer.ReviewAsync(request, persistedBriefing, persistedOutline, persistedChapter, CancellationToken.None);
    Require(review.Decision == EditorialReviewDecision.Pass, "Structured independent review did not PASS.");
    Require(review.ReviewerId.Contains("reviewer-free", StringComparison.Ordinal), "Reviewer metadata was not retained.");
    Require(invoker.Purposes.SequenceEqual(["briefing", "outline", "chapter", "independent-review"]), "Production adapters invoked stages in an unexpected order.");
}

static void VerifyNaturalLanguageDefaults()
{
    var constructor = typeof(NaturalLanguageEditorialJourneyService).GetConstructors().Single();
    Require(constructor.GetParameters().Length == 1, "Natural-language service must wrap the deterministic orchestrator directly.");
    Require(EditorialArtifactIdFactory.Briefing("vs130") == "vs130.draft.briefing", "Canonical briefing ID mismatch.");
    Require(EditorialArtifactIdFactory.Release("vs130", "editorial-proof") == "vs130.release.editorial-proof", "Canonical release ID mismatch.");
}

static PersistedEditorialArtifact Artifact(string id, string content)
{
    var bytes = System.Text.Encoding.UTF8.GetBytes(content);
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    return new PersistedEditorialArtifact(id, 1, hash, "text/markdown", bytes.Length);
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class ScriptedModelInvoker : IEditorialModelInvoker
{
    public List<string> Purposes { get; } = [];

    public ValueTask<EditorialModelExecution> InvokeAsync(
        string purpose,
        string prompt,
        string context,
        IReadOnlyList<EditorialModelCandidate> candidates,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Purposes.Add(purpose);
        var content = purpose switch
        {
            "briefing" => "# Briefing\n\nPublico adulto, misterio literario, tono sobrio y criterios de coherencia verificables.",
            "outline" => "# Esquema\n\n1. Hallazgo del archivo.\n2. Primera desaparicion.\n3. Confrontacion y cierre.",
            "chapter" => "# Capitulo 1\n\nLa archivista abrio el legajo sellado y encontro la fecha de una desaparicion que aun no habia ocurrido. La lluvia golpeaba los cristales del archivo municipal mientras ella comparaba firmas, sellos y huecos deliberados. Cada ausencia formaba un patron. Al final de la pagina aparecia su propio nombre.",
            "independent-review" => "DECISION: PASS\nREASONS: Coherencia, voz, tension y cierre adecuados.",
            _ => throw new InvalidOperationException($"Unexpected purpose {purpose}"),
        };
        var model = purpose == "independent-review" ? "reviewer-free" : "writer-free";
        return ValueTask.FromResult(new EditorialModelExecution("opencode", model, "prompt-hash", "context-hash", 15, content));
    }
}

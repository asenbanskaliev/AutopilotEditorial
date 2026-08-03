using BookStudio.Autopilot.EditorialJourney;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

await VerifyHappyPathAndResumeAsync();
await VerifyFingerprintConflictAsync();
await VerifyReviewFailureEvidenceAsync();
VerifyCanonicalArtifactIds();
Console.WriteLine("EDITORIAL_JOURNEY_TESTS_PASS");

static async Task VerifyHappyPathAndResumeAsync()
{
    var generator = new FakeGenerator();
    var gateway = new FakeArtifactGateway();
    var reviewer = new FakeReviewer(EditorialReviewDecision.Pass);
    var checkpoints = new InMemoryCheckpointStore();
    var progress = new RecordingProgressSink();
    var orchestrator = new DeterministicEditorialJourneyOrchestrator(
        generator,
        gateway,
        reviewer,
        checkpoints,
        progress,
        TimeProvider.System);

    var request = new EditorialJourneyRequest(
        "navarra-misterio",
        "Una archivera descubre registros que predicen desapariciones.",
        "es-ES",
        "Los registros borrados",
        1);

    var first = await orchestrator.RunAsync(request);
    Require(first.Completed, "First journey did not complete.");
    Require(!first.Resumed, "First journey was incorrectly marked resumed.");
    Require(first.ReviewDecision == EditorialReviewDecision.Pass, "Independent review did not pass.");
    Require(first.Artifacts.Count == 3, "Expected exactly three persisted editorial artifacts.");
    Require(gateway.RegisterCalls == 3, "Expected exactly three first-run registrations.");
    Require(gateway.PrepareReleaseCalls == 1, "Expected exactly one release preparation.");
    Require(generator.TotalCalls == 3, "Expected exactly three generation calls.");
    Require(first.Events.Any(item => item.Code == "journey_complete"), "Completion evidence missing.");

    var second = await orchestrator.RunAsync(request);
    Require(second.Completed && second.Resumed, "Second journey did not resume completed state.");
    Require(gateway.RegisterCalls == 3, "Resume duplicated draft registration.");
    Require(gateway.PrepareReleaseCalls == 1, "Resume duplicated release preparation.");
    Require(generator.TotalCalls == 3, "Resume duplicated model generation.");
    Require(progress.Events.Any(item => item.Code == "artifact_verified"), "Progress events were not reported.");
}

static async Task VerifyFingerprintConflictAsync()
{
    var checkpoints = new InMemoryCheckpointStore();
    var orchestrator = new DeterministicEditorialJourneyOrchestrator(
        new FakeGenerator(),
        new FakeArtifactGateway(),
        new FakeReviewer(EditorialReviewDecision.Pass),
        checkpoints);

    var original = new EditorialJourneyRequest("conflict-project", "Idea original", Title: "Original");
    await orchestrator.RunAsync(original);

    try
    {
        await orchestrator.RunAsync(original with { Idea = "Idea diferente" });
        throw new InvalidOperationException("Fingerprint conflict was not rejected.");
    }
    catch (EditorialJourneyException exception)
    {
        Require(exception.Code == "request_fingerprint_conflict", "Unexpected fingerprint failure code.");
    }
}

static async Task VerifyReviewFailureEvidenceAsync()
{
    var checkpoints = new InMemoryCheckpointStore();
    var gateway = new FakeArtifactGateway();
    var orchestrator = new DeterministicEditorialJourneyOrchestrator(
        new FakeGenerator(),
        gateway,
        new FakeReviewer(EditorialReviewDecision.Revise),
        checkpoints);
    var request = new EditorialJourneyRequest("review-project", "Idea que requiere revisión", Title: "Prueba");

    try
    {
        await orchestrator.RunAsync(request);
        throw new InvalidOperationException("Review revision was not rejected.");
    }
    catch (EditorialJourneyException exception)
    {
        Require(exception.Code == "review_requires_revision", "Unexpected review failure code.");
    }

    var checkpoint = await checkpoints.LoadAsync(request.ProjectId, CancellationToken.None);
    Require(checkpoint is not null, "Failure checkpoint was not persisted.");
    Require(checkpoint.Events.Any(item => item.Status == "FAIL" && item.Code == "review_requires_revision"),
        "Sanitized failure evidence was not persisted.");
    Require(gateway.PrepareReleaseCalls == 0, "Release was prepared after review failure.");
}

static void VerifyCanonicalArtifactIds()
{
    Require(EditorialArtifactIdFactory.Briefing("demo") == "demo.draft.briefing", "Briefing ID mismatch.");
    Require(EditorialArtifactIdFactory.Outline("demo") == "demo.draft.outline", "Outline ID mismatch.");
    Require(EditorialArtifactIdFactory.Chapter("demo", 7) == "demo.draft.chapter-07", "Chapter ID mismatch.");
    Require(EditorialArtifactIdFactory.Release("demo", "editorial-proof") == "demo.release.editorial-proof", "Release ID mismatch.");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FakeGenerator : IEditorialContentGenerator
{
    public int TotalCalls { get; private set; }

    public ValueTask<GeneratedEditorialContent> GenerateBriefingAsync(EditorialJourneyRequest request, CancellationToken cancellationToken)
    {
        TotalCalls++;
        return ValueTask.FromResult(Content("# Briefing\n\nObjetivo, lector y promesa editorial."));
    }

    public ValueTask<GeneratedEditorialContent> GenerateOutlineAsync(EditorialJourneyRequest request, PersistedEditorialArtifact briefing, CancellationToken cancellationToken)
    {
        TotalCalls++;
        return ValueTask.FromResult(Content("# Esquema\n\n1. Hallazgo\n2. Investigación\n3. Revelación"));
    }

    public ValueTask<GeneratedEditorialContent> GenerateChapterAsync(EditorialJourneyRequest request, PersistedEditorialArtifact briefing, PersistedEditorialArtifact outline, CancellationToken cancellationToken)
    {
        TotalCalls++;
        return ValueTask.FromResult(Content("# Capítulo 1\n\nLa archivera abrió la caja sellada y encontró una fecha que todavía no había ocurrido."));
    }

    private static GeneratedEditorialContent Content(string markdown) =>
        new(markdown, "test-provider", "test-model", Sha(markdown));

    private static string Sha(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

sealed class FakeReviewer(EditorialReviewDecision decision) : IEditorialIndependentReviewer
{
    public ValueTask<EditorialReviewResult> ReviewAsync(
        EditorialJourneyRequest request,
        PersistedEditorialArtifact briefing,
        PersistedEditorialArtifact outline,
        PersistedEditorialArtifact chapter,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EditorialReviewResult(decision, decision == EditorialReviewDecision.Pass ? [] : ["continuity_gap"], "independent-test-reviewer"));
}

sealed class FakeArtifactGateway : IEditorialArtifactGateway
{
    private readonly Dictionary<(string Id, int Version), PersistedEditorialArtifact> _artifacts = [];
    private readonly Dictionary<(string Id, int Version), EditorialReleaseResult> _releases = [];

    public int RegisterCalls { get; private set; }
    public int PrepareReleaseCalls { get; private set; }

    public ValueTask<PersistedEditorialArtifact?> GetAsync(string projectId, string artifactId, int version, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_artifacts.GetValueOrDefault((artifactId, version)));

    public ValueTask<PersistedEditorialArtifact> RegisterAsync(
        string projectId,
        string artifactId,
        int expectedVersion,
        string mediaType,
        string content,
        CancellationToken cancellationToken)
    {
        RegisterCalls++;
        var bytes = Encoding.UTF8.GetBytes(content);
        var artifact = new PersistedEditorialArtifact(
            artifactId,
            expectedVersion,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            mediaType,
            bytes.LongLength);
        _artifacts.Add((artifactId, expectedVersion), artifact);
        return ValueTask.FromResult(artifact);
    }

    public ValueTask<EditorialValidationResult> ValidateAsync(string projectId, string artifactId, int version, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EditorialValidationResult(
            _artifacts.ContainsKey((artifactId, version)),
            [],
            new ReadOnlyDictionary<string, long>(new Dictionary<string, long> { ["words"] = 12 })));

    public ValueTask<EditorialReleaseResult?> GetReleaseAsync(string projectId, string releaseArtifactId, int version, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_releases.GetValueOrDefault((releaseArtifactId, version)));

    public ValueTask<EditorialReleaseResult> PrepareReleaseAsync(
        string projectId,
        string releaseId,
        string title,
        string language,
        PersistedEditorialArtifact manuscript,
        CancellationToken cancellationToken)
    {
        PrepareReleaseCalls++;
        var id = EditorialArtifactIdFactory.Release(projectId, releaseId);
        var release = new EditorialReleaseResult(id, 1, manuscript.Sha256);
        _releases.Add((id, 1), release);
        return ValueTask.FromResult(release);
    }

    public ValueTask<EditorialPreflightResult> PreflightAsync(string projectId, EditorialReleaseResult release, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EditorialPreflightResult(_releases.ContainsKey((release.ReleaseArtifactId, release.Version)), []));
}

sealed class InMemoryCheckpointStore : IEditorialJourneyCheckpointStore
{
    private readonly Dictionary<string, EditorialJourneyCheckpoint> _items = new(StringComparer.Ordinal);

    public ValueTask<EditorialJourneyCheckpoint?> LoadAsync(string projectId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_items.GetValueOrDefault(projectId));

    public ValueTask SaveAsync(EditorialJourneyCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        _items[checkpoint.ProjectId] = checkpoint;
        return ValueTask.CompletedTask;
    }
}

sealed class RecordingProgressSink : IEditorialJourneyProgressSink
{
    public List<EditorialJourneyEvent> Events { get; } = [];

    public ValueTask ReportAsync(EditorialJourneyEvent journeyEvent, CancellationToken cancellationToken)
    {
        Events.Add(journeyEvent);
        return ValueTask.CompletedTask;
    }
}

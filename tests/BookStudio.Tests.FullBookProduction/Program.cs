using System.Security.Cryptography;
using System.Text;
using BookStudio.Autopilot.EditorialJourney;

var root = Path.Combine(Path.GetTempPath(), "bookstudio-vs132-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var request = new FullBookProductionRequest(
        "vs132-book",
        "El archivo de las ausencias",
        "es-ES",
        "Una archivera descubre un patrón de desapariciones en expedientes borrados.",
        ChapterCount: 8,
        TargetWordsPerChapter: 400,
        ContextBudgetCharacters: 4000);

    var planner = new FakePlanner();
    var repository = new FakeRepository();
    var summarizer = new FakeSummarizer();
    var firstGenerator = new FakeGenerator(failOnceAtChapter: 4);
    var checkpoints = new JsonFileFullBookCheckpointStore(root);
    var first = new FullBookAutonomousProductionOrchestrator(planner, firstGenerator, repository, checkpoints, summarizer);

    var interrupted = false;
    try { await first.RunAsync(request); }
    catch (InvalidOperationException exception) when (exception.Message == "simulated_restart") { interrupted = true; }
    Require(interrupted, "first production was not interrupted");
    Require(repository.SaveCounts.Values.Sum() == 3, "expected three chapters before restart");

    var secondGenerator = new FakeGenerator();
    var second = new FullBookAutonomousProductionOrchestrator(planner, secondGenerator, repository, new JsonFileFullBookCheckpointStore(root), summarizer);
    var result = await second.RunAsync(request);

    Require(result.Completed && result.Resumed, "full book did not resume and complete");
    Require(result.Chapters.Count == 8, "full book chapter count mismatch");
    Require(result.Chapters.Keys.OrderBy(x => x).SequenceEqual(Enumerable.Range(1, 8)), "chapter sequence mismatch");
    Require(repository.SaveCounts.Count == 8 && repository.SaveCounts.Values.All(value => value == 1), "duplicate chapter writes detected");
    Require(secondGenerator.Calls.SequenceEqual(Enumerable.Range(4, 5)), "resume regenerated completed chapters");
    Require(planner.Calls == 1, "resume recreated the chapter plan");
    Require(result.TotalWords >= 8 * 260 && result.TotalWords <= 8 * 540, "book word count outside bounded targets");
    Require(secondGenerator.ContextLengths.All(length => length <= request.ContextBudgetCharacters), "context budget exceeded");

    var thirdGenerator = new FakeGenerator();
    var third = new FullBookAutonomousProductionOrchestrator(planner, thirdGenerator, repository, new JsonFileFullBookCheckpointStore(root), summarizer);
    var completedResume = await third.RunAsync(request);
    Require(completedResume.Completed && completedResume.Resumed, "completed book did not resume");
    Require(thirdGenerator.Calls.Count == 0, "completed resume generated chapters");

    var conflict = request with { Title = "Otro libro" };
    var conflictDetected = false;
    try { await third.RunAsync(conflict); }
    catch (EditorialJourneyException exception) when (exception.Code == "full_book_request_conflict") { conflictDetected = true; }
    Require(conflictDetected, "request fingerprint conflict was not detected");

    Console.WriteLine("PASS VS-132 full-book autonomous production");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FakePlanner : IFullBookPlanner
{
    public int Calls { get; private set; }
    public ValueTask<FullBookPlan> CreatePlanAsync(FullBookProductionRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        var chapters = Enumerable.Range(1, request.ChapterCount)
            .Select(number => new FullBookChapterPlan(number, $"Hito {number}", $"Resolver el objetivo narrativo {number}", $"Mantener la pista {number}"))
            .ToArray();
        return ValueTask.FromResult(new FullBookPlan(chapters, "Una investigación familiar y documental.", "La protagonista elige revelar la verdad."));
    }
}

sealed class FakeGenerator(int? failOnceAtChapter = null) : IFullBookChapterGenerator
{
    private bool _failed;
    public List<int> Calls { get; } = [];
    public List<int> ContextLengths { get; } = [];

    public ValueTask<FullBookGeneratedChapter> GenerateAsync(FullBookProductionRequest request, FullBookChapterPlan chapter, string boundedContext, CancellationToken cancellationToken)
    {
        Calls.Add(chapter.Number);
        ContextLengths.Add(boundedContext.Length);
        if (!_failed && failOnceAtChapter == chapter.Number)
        {
            _failed = true;
            throw new InvalidOperationException("simulated_restart");
        }
        var body = string.Join(' ', Enumerable.Range(1, 330).Select(index => $"palabra{chapter.Number}_{index}"));
        var markdown = $"# Capítulo {chapter.Number}: {chapter.Title}\n\n{body}\n\nLa pista {chapter.Number} cambia el rumbo de la investigación.";
        return ValueTask.FromResult(new FullBookGeneratedChapter(chapter.Number, markdown, "test", "deterministic", Hash(markdown)));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

sealed class FakeRepository : IFullBookChapterRepository
{
    private readonly Dictionary<(string ProjectId, int Number), FullBookPersistedChapter> _items = [];
    public Dictionary<int, int> SaveCounts { get; } = [];

    public ValueTask<FullBookPersistedChapter?> GetAsync(string projectId, int chapterNumber, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_items.GetValueOrDefault((projectId, chapterNumber)));

    public ValueTask<FullBookPersistedChapter> SaveAsync(string projectId, FullBookGeneratedChapter chapter, string summary, CancellationToken cancellationToken)
    {
        SaveCounts[chapter.Number] = SaveCounts.GetValueOrDefault(chapter.Number) + 1;
        if (_items.ContainsKey((projectId, chapter.Number))) throw new InvalidOperationException("duplicate_write");
        var bytes = Encoding.UTF8.GetBytes(chapter.Markdown);
        var persisted = new FullBookPersistedChapter(
            chapter.Number,
            EditorialArtifactIdFactory.Chapter(projectId, chapter.Number),
            1,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            chapter.Markdown.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            summary);
        _items[(projectId, chapter.Number)] = persisted;
        return ValueTask.FromResult(persisted);
    }
}

sealed class FakeSummarizer : IFullBookSummarizer
{
    public ValueTask<string> SummarizeAsync(string previousRollingSummary, FullBookPersistedChapter chapter, CancellationToken cancellationToken) =>
        ValueTask.FromResult($"{previousRollingSummary}\nC{chapter.Number}:{chapter.Summary}".Trim());
}

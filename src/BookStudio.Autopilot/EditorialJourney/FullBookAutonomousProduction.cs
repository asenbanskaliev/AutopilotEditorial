using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BookStudio.Autopilot.EditorialJourney;

public sealed record FullBookProductionRequest(
    string ProjectId,
    string Title,
    string Language,
    string Idea,
    int ChapterCount,
    int TargetWordsPerChapter,
    int ContextBudgetCharacters = 12000)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(Language);
        ArgumentException.ThrowIfNullOrWhiteSpace(Idea);
        if (ChapterCount is < 2 or > 80) throw new ArgumentOutOfRangeException(nameof(ChapterCount));
        if (TargetWordsPerChapter is < 300 or > 10000) throw new ArgumentOutOfRangeException(nameof(TargetWordsPerChapter));
        if (ContextBudgetCharacters is < 2000 or > 100000) throw new ArgumentOutOfRangeException(nameof(ContextBudgetCharacters));
    }
}

public sealed record FullBookChapterPlan(int Number, string Title, string Goal, string ContinuityAnchor);
public sealed record FullBookPlan(IReadOnlyList<FullBookChapterPlan> Chapters, string Premise, string EndingPromise);
public sealed record FullBookGeneratedChapter(int Number, string Markdown, string Provider, string Model, string PromptHash);
public sealed record FullBookPersistedChapter(int Number, string ArtifactId, int Version, string Sha256, int WordCount, string Summary);
public sealed record FullBookProductionCheckpoint(
    string ProjectId,
    string RequestFingerprint,
    FullBookPlan Plan,
    IReadOnlyDictionary<int, FullBookPersistedChapter> Chapters,
    string RollingSummary,
    bool Completed,
    DateTimeOffset UpdatedAtUtc);
public sealed record FullBookProductionResult(
    bool Completed,
    bool Resumed,
    FullBookPlan Plan,
    IReadOnlyDictionary<int, FullBookPersistedChapter> Chapters,
    string RollingSummary,
    int TotalWords);

public interface IFullBookPlanner
{
    ValueTask<FullBookPlan> CreatePlanAsync(FullBookProductionRequest request, CancellationToken cancellationToken);
}

public interface IFullBookChapterGenerator
{
    ValueTask<FullBookGeneratedChapter> GenerateAsync(
        FullBookProductionRequest request,
        FullBookChapterPlan chapter,
        string boundedContext,
        CancellationToken cancellationToken);
}

public interface IFullBookChapterRepository
{
    ValueTask<FullBookPersistedChapter?> GetAsync(string projectId, int chapterNumber, CancellationToken cancellationToken);
    ValueTask<FullBookPersistedChapter> SaveAsync(string projectId, FullBookGeneratedChapter chapter, string summary, CancellationToken cancellationToken);
}

public interface IFullBookCheckpointStore
{
    ValueTask<FullBookProductionCheckpoint?> LoadAsync(string projectId, CancellationToken cancellationToken);
    ValueTask SaveAsync(FullBookProductionCheckpoint checkpoint, CancellationToken cancellationToken);
}

public interface IFullBookSummarizer
{
    ValueTask<string> SummarizeAsync(string previousRollingSummary, FullBookPersistedChapter chapter, CancellationToken cancellationToken);
}

public sealed class FullBookAutonomousProductionOrchestrator
{
    private readonly IFullBookPlanner _planner;
    private readonly IFullBookChapterGenerator _generator;
    private readonly IFullBookChapterRepository _chapters;
    private readonly IFullBookCheckpointStore _checkpoints;
    private readonly IFullBookSummarizer _summarizer;

    public FullBookAutonomousProductionOrchestrator(
        IFullBookPlanner planner,
        IFullBookChapterGenerator generator,
        IFullBookChapterRepository chapters,
        IFullBookCheckpointStore checkpoints,
        IFullBookSummarizer summarizer)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _chapters = chapters ?? throw new ArgumentNullException(nameof(chapters));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        _summarizer = summarizer ?? throw new ArgumentNullException(nameof(summarizer));
    }

    public async ValueTask<FullBookProductionResult> RunAsync(FullBookProductionRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        var fingerprint = Fingerprint(request);
        var checkpoint = await _checkpoints.LoadAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        var resumed = checkpoint is not null;
        if (checkpoint is not null && !string.Equals(checkpoint.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            throw new EditorialJourneyException(EditorialJourneyStage.Briefing, "full_book_request_conflict", "A different full-book request already owns this project id.");

        var plan = checkpoint?.Plan ?? await _planner.CreatePlanAsync(request, cancellationToken).ConfigureAwait(false);
        ValidatePlan(request, plan);
        var completed = checkpoint?.Chapters.ToDictionary(x => x.Key, x => x.Value) ?? new Dictionary<int, FullBookPersistedChapter>();
        var rollingSummary = checkpoint?.RollingSummary ?? string.Empty;

        if (checkpoint?.Completed == true)
            return BuildResult(true, true, plan, completed, rollingSummary);

        foreach (var chapterPlan in plan.Chapters.OrderBy(x => x.Number))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completed.ContainsKey(chapterPlan.Number)) continue;

            var existing = await _chapters.GetAsync(request.ProjectId, chapterPlan.Number, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                completed[chapterPlan.Number] = existing;
                rollingSummary = await _summarizer.SummarizeAsync(rollingSummary, existing, cancellationToken).ConfigureAwait(false);
                await SaveCheckpointAsync(false).ConfigureAwait(false);
                continue;
            }

            var context = BuildBoundedContext(request, plan, chapterPlan, rollingSummary, completed);
            var generated = await _generator.GenerateAsync(request, chapterPlan, context, cancellationToken).ConfigureAwait(false);
            ValidateGenerated(request, chapterPlan, generated);
            var chapterSummary = BuildDeterministicChapterSummary(chapterPlan, generated.Markdown);
            var persisted = await _chapters.SaveAsync(request.ProjectId, generated, chapterSummary, cancellationToken).ConfigureAwait(false);
            ValidatePersisted(request.ProjectId, chapterPlan.Number, generated, persisted);
            completed[chapterPlan.Number] = persisted;
            rollingSummary = await _summarizer.SummarizeAsync(rollingSummary, persisted, cancellationToken).ConfigureAwait(false);
            rollingSummary = TrimFromEnd(rollingSummary, request.ContextBudgetCharacters / 2);
            await SaveCheckpointAsync(false).ConfigureAwait(false);
        }

        await SaveCheckpointAsync(true).ConfigureAwait(false);
        return BuildResult(true, resumed, plan, completed, rollingSummary);

        async ValueTask SaveCheckpointAsync(bool isComplete)
        {
            await _checkpoints.SaveAsync(new FullBookProductionCheckpoint(
                request.ProjectId,
                fingerprint,
                plan,
                new Dictionary<int, FullBookPersistedChapter>(completed),
                rollingSummary,
                isComplete,
                DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidatePlan(FullBookProductionRequest request, FullBookPlan plan)
    {
        if (plan.Chapters.Count != request.ChapterCount) throw new InvalidOperationException("The plan chapter count does not match the request.");
        var expected = Enumerable.Range(1, request.ChapterCount).ToArray();
        if (!plan.Chapters.Select(x => x.Number).OrderBy(x => x).SequenceEqual(expected))
            throw new InvalidOperationException("The chapter plan must be contiguous and one-based.");
        if (plan.Chapters.Any(x => string.IsNullOrWhiteSpace(x.Title) || string.IsNullOrWhiteSpace(x.Goal)))
            throw new InvalidOperationException("Every planned chapter requires title and goal.");
    }

    private static void ValidateGenerated(FullBookProductionRequest request, FullBookChapterPlan plan, FullBookGeneratedChapter chapter)
    {
        if (chapter.Number != plan.Number) throw new InvalidOperationException("Generated chapter number mismatch.");
        if (!chapter.Markdown.TrimStart().StartsWith($"# Capítulo {plan.Number}", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Generated chapter heading mismatch.");
        var words = CountWords(chapter.Markdown);
        var minimum = Math.Max(250, (int)Math.Floor(request.TargetWordsPerChapter * 0.65));
        var maximum = (int)Math.Ceiling(request.TargetWordsPerChapter * 1.35);
        if (words < minimum || words > maximum)
            throw new InvalidOperationException($"Generated chapter {plan.Number} has {words} words; expected {minimum}-{maximum}.");
        if (string.IsNullOrWhiteSpace(chapter.Provider) || string.IsNullOrWhiteSpace(chapter.Model) || string.IsNullOrWhiteSpace(chapter.PromptHash))
            throw new InvalidOperationException("Generated chapter metadata is incomplete.");
    }

    private static void ValidatePersisted(string projectId, int number, FullBookGeneratedChapter generated, FullBookPersistedChapter persisted)
    {
        var expectedId = EditorialArtifactIdFactory.Chapter(projectId, number);
        var expectedHash = Hash(generated.Markdown);
        if (persisted.Number != number || !string.Equals(persisted.ArtifactId, expectedId, StringComparison.Ordinal) || persisted.Version != 1 || !string.Equals(persisted.Sha256, expectedHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted chapter postcondition failed.");
    }

    private static string BuildBoundedContext(FullBookProductionRequest request, FullBookPlan plan, FullBookChapterPlan current, string rollingSummary, IReadOnlyDictionary<int, FullBookPersistedChapter> completed)
    {
        var recent = completed.Values.OrderByDescending(x => x.Number).Take(3).OrderBy(x => x.Number)
            .Select(x => $"Capítulo {x.Number}: {x.Summary}");
        var context = $"""
TÍTULO: {request.Title}
IDEA: {request.Idea}
PREMISA: {plan.Premise}
PROMESA FINAL: {plan.EndingPromise}
CAPÍTULO ACTUAL: {current.Number} - {current.Title}
OBJETIVO: {current.Goal}
ANCLA DE CONTINUIDAD: {current.ContinuityAnchor}
RESUMEN ACUMULADO:
{rollingSummary}
CAPÍTULOS RECIENTES:
{string.Join('\n', recent)}
""";
        return TrimFromEnd(context, request.ContextBudgetCharacters);
    }

    private static string BuildDeterministicChapterSummary(FullBookChapterPlan plan, string markdown)
    {
        var plain = string.Join(' ', markdown.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var excerpt = plain.Length <= 500 ? plain : plain[..500];
        return $"{plan.Title}: {plan.Goal}. {excerpt}";
    }

    private static FullBookProductionResult BuildResult(bool completed, bool resumed, FullBookPlan plan, IReadOnlyDictionary<int, FullBookPersistedChapter> chapters, string rollingSummary) =>
        new(completed, resumed, plan, new Dictionary<int, FullBookPersistedChapter>(chapters), rollingSummary, chapters.Values.Sum(x => x.WordCount));

    private static int CountWords(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    private static string TrimFromEnd(string value, int limit) => value.Length <= limit ? value : value[^limit..];
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Fingerprint(FullBookProductionRequest request) => Hash(JsonSerializer.Serialize(request));
}

public sealed class JsonFileFullBookCheckpointStore : IFullBookCheckpointStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonFileFullBookCheckpointStore(string root)
    {
        _root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));
        Directory.CreateDirectory(_root);
    }

    public async ValueTask<FullBookProductionCheckpoint?> LoadAsync(string projectId, CancellationToken cancellationToken)
    {
        var path = PathFor(projectId);
        if (!File.Exists(path)) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<FullBookProductionCheckpoint>(stream, _json, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask SaveAsync(FullBookProductionCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(checkpoint.ProjectId);
            var temporary = path + ".tmp";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, checkpoint, _json, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { _gate.Release(); }
    }

    private string PathFor(string projectId)
    {
        var safe = string.Concat(projectId.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        return Path.Combine(_root, safe + ".full-book-checkpoint.json");
    }
}

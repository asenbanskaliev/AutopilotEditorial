using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BookStudio.Autopilot.EditorialJourney;

public sealed class OpenCodeCommercialBookPlanner : IFullBookPlanner
{
    private readonly IEditorialModelInvoker _models;
    private readonly EditorialJourneyProductionOptions _options;

    public OpenCodeCommercialBookPlanner(IEditorialModelInvoker models, EditorialJourneyProductionOptions options)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<FullBookPlan> CreatePlanAsync(FullBookProductionRequest request, CancellationToken cancellationToken)
    {
        var prompt = $"""
Actúa como novelista y editor senior. Diseña un libro comercial original titulado '{request.Title}' en {request.Language}.
Idea: {request.Idea}
Número exacto de capítulos: {request.ChapterCount}.
Devuelve JSON estricto sin Markdown con esta forma:
{{"premise":"...","endingPromise":"...","chapters":[{{"number":1,"title":"...","goal":"...","continuityAnchor":"..."}}]}}
Cada capítulo debe avanzar causalmente, tener conflicto propio y ancla de continuidad concreta. No uses placeholders ni texto genérico.
""";
        var execution = await _models.InvokeAsync("commercial-book-plan", prompt, request.Idea, _options.WriterModels, _options.GenerationTimeout, cancellationToken).ConfigureAwait(false);
        var dto = JsonSerializer.Deserialize<PlanDto>(ExtractJson(execution.Content), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("The commercial plan response was empty.");
        var chapters = dto.Chapters?.Select(x => new FullBookChapterPlan(x.Number, x.Title ?? string.Empty, x.Goal ?? string.Empty, x.ContinuityAnchor ?? string.Empty)).ToArray() ?? [];
        return new FullBookPlan(chapters, dto.Premise ?? string.Empty, dto.EndingPromise ?? string.Empty);
    }

    private static string ExtractJson(string value)
    {
        var start = value.IndexOf('{');
        var end = value.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidDataException("The commercial plan did not contain JSON.");
        return value[start..(end + 1)];
    }

    private sealed record PlanDto(string? Premise, string? EndingPromise, ChapterDto[]? Chapters);
    private sealed record ChapterDto(int Number, string? Title, string? Goal, string? ContinuityAnchor);
}

public sealed class OpenCodeCommercialChapterGenerator : IFullBookChapterGenerator
{
    private readonly IEditorialModelInvoker _models;
    private readonly EditorialJourneyProductionOptions _options;
    private readonly Dictionary<int, string> _chapterHashes = [];

    public OpenCodeCommercialChapterGenerator(IEditorialModelInvoker models, EditorialJourneyProductionOptions options)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<FullBookGeneratedChapter> GenerateAsync(FullBookProductionRequest request, FullBookChapterPlan chapter, string boundedContext, CancellationToken cancellationToken)
    {
        var prompt = $"""
Escribe el capítulo {chapter.Number} de {request.ChapterCount} de la obra comercial '{request.Title}' en {request.Language}.
Título del capítulo: {chapter.Title}
Objetivo dramático: {chapter.Goal}
Ancla de continuidad: {chapter.ContinuityAnchor}
Extensión objetivo: {request.TargetWordsPerChapter} palabras.
Produce solo Markdown comenzando exactamente por '# Capítulo {chapter.Number}: {chapter.Title}'.
Requisitos: escenas concretas, causalidad, voz estable, diálogo natural cuando proceda, detalles sensoriales, conflicto, cambio irreversible y cierre que impulse el siguiente capítulo. Evita resumen, relleno, listas, metaexplicaciones, placeholders y repetición de frases.
""";
        var execution = await _models.InvokeAsync($"commercial-chapter-{chapter.Number:D2}", prompt, boundedContext, _options.WriterModels, _options.GenerationTimeout, cancellationToken).ConfigureAwait(false);
        CommercialManuscriptPolicy.ValidateChapter(execution.Content, chapter.Number, request.TargetWordsPerChapter, _chapterHashes.Values);
        var hash = Hash(execution.Content);
        _chapterHashes[chapter.Number] = hash;
        return new FullBookGeneratedChapter(chapter.Number, execution.Content.Trim(), execution.Provider, execution.Model, execution.PromptHash);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public static class CommercialManuscriptPolicy
{
    private static readonly string[] Forbidden = ["lorem ipsum", "placeholder", "texto de prueba", "contenido pendiente", "como modelo de ia", "as an ai"];

    public static void ValidateChapter(string markdown, int chapterNumber, int targetWords, IEnumerable<string>? priorHashes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);
        var normalized = markdown.Trim();
        if (!normalized.StartsWith($"# Capítulo {chapterNumber}", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Commercial chapter heading mismatch.");
        var lower = normalized.ToLowerInvariant();
        if (Forbidden.Any(lower.Contains)) throw new InvalidDataException("Commercial chapter contains placeholder or model-wrapper text.");
        var words = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var minimum = Math.Max(600, (int)(targetWords * 0.70));
        var maximum = (int)(targetWords * 1.35);
        if (words.Length < minimum || words.Length > maximum) throw new InvalidDataException($"Commercial chapter word count {words.Length} is outside {minimum}-{maximum}.");
        var sentences = normalized.Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length >= 25).Select(Normalize).ToArray();
        if (sentences.Length < 12) throw new InvalidDataException("Commercial chapter lacks sufficient narrative development.");
        var repeated = sentences.GroupBy(x => x, StringComparer.Ordinal).Max(x => x.Count());
        if (repeated > 2) throw new InvalidDataException("Commercial chapter contains excessive repeated sentences.");
        var uniqueRatio = sentences.Distinct(StringComparer.Ordinal).Count() / (double)sentences.Length;
        if (uniqueRatio < 0.82) throw new InvalidDataException("Commercial chapter is excessively repetitive.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        if (priorHashes?.Contains(hash, StringComparer.Ordinal) == true) throw new InvalidDataException("Commercial chapter duplicates an earlier chapter.");
    }

    public static void ValidateBook(IReadOnlyList<string> chapters, int minimumTotalWords)
    {
        if (chapters.Count < 8) throw new InvalidDataException("A commercial acceptance book requires at least eight chapters.");
        var totalWords = chapters.Sum(x => x.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        if (totalWords < minimumTotalWords) throw new InvalidDataException("Commercial manuscript is below the required total word count.");
        var hashes = chapters.Select(x => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(x.Trim())))).ToArray();
        if (hashes.Distinct(StringComparer.Ordinal).Count() != hashes.Length) throw new InvalidDataException("Commercial manuscript contains duplicate chapters.");
    }

    private static string Normalize(string value) => string.Join(' ', value.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

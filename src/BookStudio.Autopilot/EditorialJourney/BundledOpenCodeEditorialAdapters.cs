namespace BookStudio.Autopilot.EditorialJourney;

public interface IEditorialGeneratedContentSnapshot
{
    ValueTask<(string Briefing, string Outline, string Chapter)> GetBundleAsync(
        EditorialJourneyRequest request,
        CancellationToken cancellationToken);
}

public sealed class BundledOpenCodeEditorialContentGenerator : IEditorialContentGenerator, IEditorialGeneratedContentSnapshot
{
    private const string BriefingMarker = "<!-- BOOKSTUDIO_BRIEFING -->";
    private const string OutlineMarker = "<!-- BOOKSTUDIO_OUTLINE -->";
    private const string ChapterMarker = "<!-- BOOKSTUDIO_CHAPTER -->";
    private readonly IEditorialModelInvoker _models;
    private readonly EditorialJourneyProductionOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _requestKey;
    private (string Briefing, string Outline, string Chapter, EditorialModelExecution Execution)? _bundle;

    public BundledOpenCodeEditorialContentGenerator(IEditorialModelInvoker models, EditorialJourneyProductionOptions options)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<GeneratedEditorialContent> GenerateBriefingAsync(EditorialJourneyRequest request, CancellationToken cancellationToken)
    {
        var bundle = await EnsureBundleAsync(request, cancellationToken).ConfigureAwait(false);
        return ToGenerated(bundle.Briefing, bundle.Execution);
    }

    public async ValueTask<GeneratedEditorialContent> GenerateOutlineAsync(EditorialJourneyRequest request, PersistedEditorialArtifact briefing, CancellationToken cancellationToken)
    {
        var bundle = await EnsureBundleAsync(request, cancellationToken).ConfigureAwait(false);
        return ToGenerated(bundle.Outline, bundle.Execution);
    }

    public async ValueTask<GeneratedEditorialContent> GenerateChapterAsync(EditorialJourneyRequest request, PersistedEditorialArtifact briefing, PersistedEditorialArtifact outline, CancellationToken cancellationToken)
    {
        var bundle = await EnsureBundleAsync(request, cancellationToken).ConfigureAwait(false);
        return ToGenerated(bundle.Chapter, bundle.Execution);
    }

    public async ValueTask<(string Briefing, string Outline, string Chapter)> GetBundleAsync(EditorialJourneyRequest request, CancellationToken cancellationToken)
    {
        var bundle = await EnsureBundleAsync(request, cancellationToken).ConfigureAwait(false);
        return (bundle.Briefing, bundle.Outline, bundle.Chapter);
    }

    private async ValueTask<(string Briefing, string Outline, string Chapter, EditorialModelExecution Execution)> EnsureBundleAsync(EditorialJourneyRequest request, CancellationToken cancellationToken)
    {
        var key = $"{request.ProjectId}|{request.Idea}|{request.Title}|{request.Language}|{request.ChapterNumber}";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_bundle is not null && string.Equals(_requestKey, key, StringComparison.Ordinal)) return _bundle.Value;
            if (_bundle is not null) throw new EditorialJourneyException(EditorialJourneyStage.Briefing, "bundle_request_conflict", "The bundled generator is already bound to another request.");

            var prompt = $"""
Actua como un equipo editorial profesional y produce una muestra coherente para el libro '{request.Title}' en {request.Language}.
Idea: {request.Idea}
Devuelve exactamente tres secciones, usando estos marcadores literales en líneas independientes:
{BriefingMarker}
Un briefing Markdown conciso con publico, promesa, genero, tono, alcance, riesgos y criterios de calidad.
{OutlineMarker}
Un esquema Markdown con arco, ocho hitos de capitulos, objetivos, conflicto, continuidad y criterios verificables.
{ChapterMarker}
Un capitulo de muestra de 450 a 700 palabras que comience con '# Capítulo 1', tenga objetivo, tensión, continuidad y cierre con gancho.
No devuelvas JSON, bloques de codigo, explicaciones de herramientas ni texto antes del primer marcador.
""";
            var execution = await _models.InvokeAsync("editorial-bundle", prompt, request.Idea, _options.WriterModels, _options.GenerationTimeout, cancellationToken).ConfigureAwait(false);
            var parsed = Parse(execution.Content);
            _requestKey = key;
            _bundle = (parsed.Briefing, parsed.Outline, parsed.Chapter, execution);
            return _bundle.Value;
        }
        finally { _gate.Release(); }
    }

    private static (string Briefing, string Outline, string Chapter) Parse(string content)
    {
        var briefingAt = content.IndexOf(BriefingMarker, StringComparison.Ordinal);
        var outlineAt = content.IndexOf(OutlineMarker, StringComparison.Ordinal);
        var chapterAt = content.IndexOf(ChapterMarker, StringComparison.Ordinal);
        if (briefingAt < 0 || outlineAt <= briefingAt || chapterAt <= outlineAt)
            throw new EditorialJourneyException(EditorialJourneyStage.Briefing, "bundle_markers_missing", "OpenCode did not return the required editorial bundle markers.");
        var briefing = content[(briefingAt + BriefingMarker.Length)..outlineAt].Trim();
        var outline = content[(outlineAt + OutlineMarker.Length)..chapterAt].Trim();
        var chapter = content[(chapterAt + ChapterMarker.Length)..].Trim();
        if (briefing.Length < 120 || outline.Length < 180 || chapter.Length < 600 || !chapter.StartsWith('#'))
            throw new EditorialJourneyException(EditorialJourneyStage.Briefing, "bundle_content_invalid", "The generated editorial bundle was incomplete.");
        return (briefing, outline, chapter);
    }

    private static GeneratedEditorialContent ToGenerated(string content, EditorialModelExecution execution) => new(content, execution.Provider, execution.Model, execution.PromptHash);
}

public sealed class ContentAwareOpenCodeIndependentReviewer : IEditorialIndependentReviewer
{
    private readonly IEditorialGeneratedContentSnapshot _snapshot;
    private readonly IEditorialModelInvoker _models;
    private readonly EditorialJourneyProductionOptions _options;

    public ContentAwareOpenCodeIndependentReviewer(IEditorialGeneratedContentSnapshot snapshot, IEditorialModelInvoker models, EditorialJourneyProductionOptions options)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<EditorialReviewResult> ReviewAsync(EditorialJourneyRequest request, PersistedEditorialArtifact briefing, PersistedEditorialArtifact outline, PersistedEditorialArtifact chapter, CancellationToken cancellationToken)
    {
        var content = await _snapshot.GetBundleAsync(request, cancellationToken).ConfigureAwait(false);
        var prompt = $"""
Eres un revisor editorial independiente y adversarial que decide si esta muestra es suficientemente coherente y segura para continuar a preflight técnico.
Evalua coherencia entre briefing, esquema y capitulo, continuidad, contradicciones materiales, voz, ritmo, repeticion grave, claridad, cierre y riesgos de publicacion.
Criterio obligatorio:
- PASS: la muestra es utilizable y no contiene un defecto material que impida continuar. Incluye como observaciones las mejoras opcionales o de pulido.
- REVISE: existe al menos un defecto concreto que impide continuar, pero puede corregirse.
- BLOCKED: contenido inutilizable, inseguro o incompatible con la idea.
No uses REVISE por preferencias estilisticas, mejoras opcionales ni porque el texto no sea perfecto.
Primera linea exacta: DECISION: PASS, DECISION: REVISE o DECISION: BLOCKED.
Despues escribe REASONS: y motivos breves. No incluyas ninguna otra cabecera antes de la decision.

BRIEFING:
{content.Briefing}

ESQUEMA:
{content.Outline}

CAPITULO:
{content.Chapter}
""";
        var execution = await _models.InvokeAsync("independent-content-review", prompt, chapter.Sha256, _options.ReviewerModels, _options.ReviewTimeout, cancellationToken).ConfigureAwait(false);
        var lines = execution.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var decision = (lines.FirstOrDefault() ?? string.Empty).ToUpperInvariant() switch
        {
            "DECISION: PASS" => EditorialReviewDecision.Pass,
            "DECISION: REVISE" => EditorialReviewDecision.Revise,
            "DECISION: BLOCKED" => EditorialReviewDecision.Blocked,
            _ => throw new EditorialJourneyException(EditorialJourneyStage.IndependentReview, "review_response_invalid", "Independent reviewer did not return a structured decision."),
        };
        return new EditorialReviewResult(decision, lines.Skip(1).Take(12).ToArray(), $"{execution.Provider}/{execution.Model}");
    }
}

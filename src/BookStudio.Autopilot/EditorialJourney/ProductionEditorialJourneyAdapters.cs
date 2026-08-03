using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BookStudio.Autopilot.EditorialJourney;

public sealed record EditorialModelCandidate(string ModelId, int Priority, bool IsFree = true);

public sealed record EditorialModelExecution(
    string Provider,
    string Model,
    string PromptHash,
    string ContextHash,
    long DurationMilliseconds,
    string Content);

public sealed record EditorialJourneyProductionOptions(
    string WorkingDirectory,
    string OpenCodeExecutable,
    string OpenCodeProvider,
    IReadOnlyList<EditorialModelCandidate> WriterModels,
    IReadOnlyList<EditorialModelCandidate> ReviewerModels,
    TimeSpan GenerationTimeout,
    TimeSpan ReviewTimeout)
{
    public static EditorialJourneyProductionOptions CreateDefault(string workingDirectory, string openCodeExecutable) =>
        new(
            workingDirectory,
            openCodeExecutable,
            "opencode",
            [
                new("opencode/deepseek-v4-flash-free", 10),
                new("opencode/north-mini-code-free", 20),
                new("opencode/nemotron-3-ultra-free", 30),
                new("opencode/mimo-v2.5-free", 40),
            ],
            [
                new("opencode/nemotron-3-ultra-free", 10),
                new("opencode/north-mini-code-free", 20),
                new("opencode/deepseek-v4-flash-free", 30),
            ],
            TimeSpan.FromMinutes(3),
            TimeSpan.FromMinutes(2));
}

public interface IEditorialModelInvoker
{
    ValueTask<EditorialModelExecution> InvokeAsync(
        string purpose,
        string prompt,
        string context,
        IReadOnlyList<EditorialModelCandidate> candidates,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class OpenCodeEditorialModelInvoker : IEditorialModelInvoker
{
    private readonly EditorialJourneyProductionOptions _options;
    private readonly ConcurrentDictionary<string, EditorialModelExecution> _cache = new(StringComparer.Ordinal);

    public OpenCodeEditorialModelInvoker(EditorialJourneyProductionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<EditorialModelExecution> InvokeAsync(
        string purpose,
        string prompt,
        string context,
        IReadOnlyList<EditorialModelCandidate> candidates,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            throw new EditorialJourneyException(EditorialJourneyStage.Briefing, "model_candidates_empty", "No approved model candidates were supplied.");
        }

        var promptHash = Hash(prompt);
        var contextHash = Hash(context ?? string.Empty);
        var cacheKey = string.Join('|', purpose, promptHash, contextHash, string.Join(',', candidates.Select(item => item.ModelId)));
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var failures = new List<string>();
        foreach (var candidate in candidates.Where(item => item.IsFree).OrderBy(item => item.Priority))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var execution = await InvokeOneAsync(candidate.ModelId, prompt, context, promptHash, contextHash, timeout, cancellationToken)
                    .ConfigureAwait(false);
                _cache.TryAdd(cacheKey, execution);
                return execution;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add($"{candidate.ModelId}:{exception.GetType().Name}");
            }
        }

        throw new EditorialJourneyException(
            EditorialJourneyStage.Briefing,
            "all_models_failed",
            $"All approved models failed for {purpose}: {string.Join(",", failures)}");
    }

    private async ValueTask<EditorialModelExecution> InvokeOneAsync(
        string model,
        string prompt,
        string context,
        string promptHash,
        string contextHash,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var combinedPrompt = string.IsNullOrWhiteSpace(context)
            ? prompt
            : $"CONTEXTO CANONICO:\n{context}\n\nTAREA:\n{prompt}";

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.OpenCodeExecutable,
            WorkingDirectory = _options.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(model);
        startInfo.ArgumentList.Add(combinedPrompt);

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        if (!process.Start())
        {
            throw new InvalidOperationException("OpenCode process could not be started.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        stopwatch.Stop();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"OpenCode exited with {process.ExitCode}: {Sanitize(stderr)}");
        }
        var content = stdout.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
        if (content.Length < 80)
        {
            throw new InvalidOperationException("OpenCode returned unexpectedly short content.");
        }
        return new EditorialModelExecution(
            _options.OpenCodeProvider,
            model,
            promptHash,
            contextHash,
            stopwatch.ElapsedMilliseconds,
            content);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Sanitize(string value) => value.Length <= 800 ? value : value[^800..];
}

public sealed class OpenCodeEditorialContentGenerator : IEditorialContentGenerator
{
    private readonly IEditorialModelInvoker _models;
    private readonly EditorialJourneyProductionOptions _options;

    public OpenCodeEditorialContentGenerator(IEditorialModelInvoker models, EditorialJourneyProductionOptions options)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<GeneratedEditorialContent> GenerateBriefingAsync(EditorialJourneyRequest request, CancellationToken cancellationToken)
    {
        var prompt = $"""
Actua como editor senior. Convierte esta idea en un briefing profesional en {request.Language}.
Incluye publico, promesa, genero, tono, alcance, restricciones, riesgos, preguntas resueltas por supuestos conservadores y criterios de calidad.
Devuelve solo Markdown editorial, sin JSON ni explicaciones de herramientas.
Idea: {request.Idea}
Titulo provisional: {request.Title}
""";
        return await GenerateAsync("briefing", prompt, string.Empty, _options.WriterModels, _options.GenerationTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<GeneratedEditorialContent> GenerateOutlineAsync(
        EditorialJourneyRequest request,
        PersistedEditorialArtifact briefing,
        CancellationToken cancellationToken)
    {
        var prompt = $"""
Crea un esquema profesional para el libro '{request.Title}' en {request.Language}.
Define arco global, capitulos, objetivo, conflicto, informacion nueva y transicion. Incluye criterios verificables por capitulo.
Devuelve solo Markdown editorial. Usa como autoridad el briefing persistido {briefing.ArtifactId} sha256={briefing.Sha256}.
""";
        return await GenerateAsync("outline", prompt, briefing.Sha256, _options.WriterModels, _options.GenerationTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<GeneratedEditorialContent> GenerateChapterAsync(
        EditorialJourneyRequest request,
        PersistedEditorialArtifact briefing,
        PersistedEditorialArtifact outline,
        CancellationToken cancellationToken)
    {
        var prompt = $"""
Escribe el capitulo {request.ChapterNumber} de '{request.Title}' en {request.Language}.
Debe comenzar con un encabezado Markdown, mantener voz consistente, evitar relleno, cerrar con avance narrativo y tener entre 700 y 1200 palabras.
Autoridades: briefing {briefing.Sha256}; esquema {outline.Sha256}.
Devuelve solo el capitulo, sin JSON, comentarios ni marcadores de herramienta.
""";
        return await GenerateAsync("chapter", prompt, briefing.Sha256 + outline.Sha256, _options.WriterModels, _options.GenerationTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<GeneratedEditorialContent> GenerateAsync(
        string purpose,
        string prompt,
        string context,
        IReadOnlyList<EditorialModelCandidate> candidates,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await _models.InvokeAsync(purpose, prompt, context, candidates, timeout, cancellationToken).ConfigureAwait(false);
        return new GeneratedEditorialContent(result.Content, result.Provider, result.Model, result.PromptHash);
    }
}

public sealed class OpenCodeIndependentEditorialReviewer : IEditorialIndependentReviewer
{
    private readonly IEditorialModelInvoker _models;
    private readonly EditorialJourneyProductionOptions _options;

    public OpenCodeIndependentEditorialReviewer(IEditorialModelInvoker models, EditorialJourneyProductionOptions options)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<EditorialReviewResult> ReviewAsync(
        EditorialJourneyRequest request,
        PersistedEditorialArtifact briefing,
        PersistedEditorialArtifact outline,
        PersistedEditorialArtifact chapter,
        CancellationToken cancellationToken)
    {
        var prompt = $"""
Eres un revisor editorial adversarial independiente. Evalua el capitulo persistido sin asumir que el escritor cumplio.
Rubrica obligatoria: coherencia con briefing y esquema, continuidad, contradicciones, voz, ritmo, repeticion, claridad, valor para lector, cierre y riesgos de publicacion.
Responde exactamente una primera linea DECISION: PASS, DECISION: REVISE o DECISION: BLOCKED.
Despues incluye REASONS: con motivos concretos y breves.
Proyecto {request.ProjectId}; briefing={briefing.Sha256}; outline={outline.Sha256}; chapter={chapter.Sha256}.
""";
        var result = await _models.InvokeAsync(
                "independent-review",
                prompt,
                briefing.Sha256 + outline.Sha256 + chapter.Sha256,
                _options.ReviewerModels,
                _options.ReviewTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        var firstLine = result.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        var decision = firstLine.ToUpperInvariant() switch
        {
            "DECISION: PASS" => EditorialReviewDecision.Pass,
            "DECISION: REVISE" => EditorialReviewDecision.Revise,
            "DECISION: BLOCKED" => EditorialReviewDecision.Blocked,
            _ => throw new EditorialJourneyException(EditorialJourneyStage.IndependentReview, "review_response_invalid", "Independent reviewer did not return a structured decision."),
        };
        var reasons = result.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Take(12)
            .ToArray();
        return new EditorialReviewResult(decision, reasons, $"{result.Provider}/{result.Model}");
    }
}

public sealed class SqliteEditorialJourneyCheckpointStore : IEditorialJourneyCheckpointStore, IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteEditorialJourneyCheckpointStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        SQLitePCL.Batteries_V2.Init();
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
CREATE TABLE IF NOT EXISTS editorial_journey_checkpoint (
    project_id TEXT PRIMARY KEY,
    request_fingerprint TEXT NOT NULL,
    checkpoint_json TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS editorial_journey_receipt (
    project_id TEXT NOT NULL,
    stage INTEGER NOT NULL,
    status TEXT NOT NULL,
    code TEXT NOT NULL,
    occurred_at_utc TEXT NOT NULL,
    PRIMARY KEY(project_id, stage, status, code, occurred_at_utc)
);
""";
        command.ExecuteNonQuery();
    }

    public async ValueTask<EditorialJourneyCheckpoint?> LoadAsync(string projectId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT checkpoint_json FROM editorial_journey_checkpoint WHERE project_id = $projectId";
            command.Parameters.AddWithValue("$projectId", projectId);
            var raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            return raw is null ? null : JsonSerializer.Deserialize<EditorialJourneyCheckpoint>(raw, _json);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(EditorialJourneyCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var json = JsonSerializer.Serialize(checkpoint, _json);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
INSERT INTO editorial_journey_checkpoint(project_id, request_fingerprint, checkpoint_json, updated_at_utc)
VALUES($projectId, $fingerprint, $json, $updated)
ON CONFLICT(project_id) DO UPDATE SET
 request_fingerprint = excluded.request_fingerprint,
 checkpoint_json = excluded.checkpoint_json,
 updated_at_utc = excluded.updated_at_utc
""";
                command.Parameters.AddWithValue("$projectId", checkpoint.ProjectId);
                command.Parameters.AddWithValue("$fingerprint", checkpoint.RequestFingerprint);
                command.Parameters.AddWithValue("$json", json);
                command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var item in checkpoint.Events)
            {
                await using var receipt = _connection.CreateCommand();
                receipt.Transaction = transaction;
                receipt.CommandText = """
INSERT OR IGNORE INTO editorial_journey_receipt(project_id, stage, status, code, occurred_at_utc)
VALUES($projectId, $stage, $status, $code, $occurred)
""";
                receipt.Parameters.AddWithValue("$projectId", checkpoint.ProjectId);
                receipt.Parameters.AddWithValue("$stage", (int)item.Stage);
                receipt.Parameters.AddWithValue("$status", item.Status);
                receipt.Parameters.AddWithValue("$code", item.Code);
                receipt.Parameters.AddWithValue("$occurred", item.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture));
                await receipt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

public sealed class JsonLineEditorialJourneyProgressSink : IEditorialJourneyProgressSink
{
    private readonly TextWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonLineEditorialJourneyProgressSink(TextWriter writer) => _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    public async ValueTask ReportAsync(EditorialJourneyEvent journeyEvent, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                type = "editorial_journey_progress",
                stage = journeyEvent.Stage.ToString(),
                status = journeyEvent.Status,
                code = journeyEvent.Code,
                occurredAtUtc = journeyEvent.OccurredAtUtc,
            })).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class NaturalLanguageEditorialJourneyService
{
    private readonly DeterministicEditorialJourneyOrchestrator _orchestrator;

    public NaturalLanguageEditorialJourneyService(DeterministicEditorialJourneyOrchestrator orchestrator)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
    }

    public ValueTask<EditorialJourneyResult> CreateBookAsync(
        string projectId,
        string idea,
        string? title = null,
        string language = "es-ES",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idea);
        var resolvedTitle = string.IsNullOrWhiteSpace(title) ? DeriveTitle(idea) : title.Trim();
        var enrichedIdea = EnrichSparseIdea(idea);
        return _orchestrator.RunAsync(new EditorialJourneyRequest(projectId, enrichedIdea, language, resolvedTitle), cancellationToken);
    }

    private static string EnrichSparseIdea(string idea)
    {
        var trimmed = idea.Trim();
        if (trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 12)
        {
            return trimmed;
        }
        return $"{trimmed}. Supuestos automaticos: publico adulto general, tono profesional accesible, estructura progresiva, sin afirmaciones factuales no verificadas y con prioridad en coherencia, claridad y utilidad.";
    }

    private static string DeriveTitle(string idea)
    {
        var words = idea.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(7);
        return string.Join(' ', words).Trim('.', ',', ';', ':');
    }
}

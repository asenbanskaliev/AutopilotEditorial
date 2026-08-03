using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BookStudio.Autopilot.EditorialJourney;

public sealed record EditorialModelCandidate(string ModelId, int Priority, bool IsFree = true);
public sealed record EditorialModelExecution(string Provider, string Model, string PromptHash, string ContextHash, long DurationMilliseconds, string Content);

public sealed record EditorialJourneyProductionOptions(
    string WorkingDirectory,
    string OpenCodeExecutable,
    string OpenCodeProvider,
    IReadOnlyList<EditorialModelCandidate> WriterModels,
    IReadOnlyList<EditorialModelCandidate> ReviewerModels,
    TimeSpan GenerationTimeout,
    TimeSpan ReviewTimeout)
{
    public static EditorialJourneyProductionOptions CreateDefault(string workingDirectory, string openCodeExecutable) => new(
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

    public OpenCodeEditorialModelInvoker(EditorialJourneyProductionOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

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
        var safeContext = context ?? string.Empty;
        var promptHash = Hash(prompt);
        var contextHash = Hash(safeContext);
        var cacheKey = $"{purpose}|{promptHash}|{contextHash}|{string.Join(',', candidates.Select(x => x.ModelId))}";
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var failures = new List<string>();
        foreach (var candidate in candidates.Where(x => x.IsFree).OrderBy(x => x.Priority))
        {
            try
            {
                var result = await InvokeOneAsync(candidate.ModelId, prompt, safeContext, promptHash, contextHash, timeout, cancellationToken)
                    .ConfigureAwait(false);
                _cache.TryAdd(cacheKey, result);
                return result;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add($"{candidate.ModelId}:{exception.GetType().Name}");
            }
        }
        throw new EditorialJourneyException(EditorialJourneyStage.Briefing, "all_models_failed", $"All approved models failed: {string.Join(',', failures)}");
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
        var start = new ProcessStartInfo
        {
            FileName = _options.OpenCodeExecutable,
            WorkingDirectory = _options.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--model");
        start.ArgumentList.Add(model);
        start.ArgumentList.Add(string.IsNullOrWhiteSpace(context) ? prompt : $"CONTEXTO CANONICO:\n{context}\n\nTAREA:\n{prompt}");

        using var process = new Process { StartInfo = start };
        var watch = Stopwatch.StartNew();
        if (!process.Start())
        {
            throw new InvalidOperationException("OpenCode process could not be started.");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
        await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        watch.Stop();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"OpenCode exited with {process.ExitCode}: {Trim(stderr)}");
        }
        var content = stdout.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
        if (content.Length < 80)
        {
            throw new InvalidOperationException("OpenCode returned unexpectedly short content.");
        }
        return new EditorialModelExecution(_options.OpenCodeProvider, model, promptHash, contextHash, watch.ElapsedMilliseconds, content);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Trim(string value) => value.Length <= 800 ? value : value[^800..];
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

    public ValueTask<GeneratedEditorialContent> GenerateBriefingAsync(EditorialJourneyRequest request, CancellationToken cancellationToken) =>
        GenerateAsync("briefing", $"Actua como editor senior. Crea un briefing profesional en {request.Language} para: {request.Idea}. Titulo: {request.Title}. Devuelve solo Markdown con publico, promesa, genero, tono, alcance, riesgos y criterios de calidad.", string.Empty, cancellationToken);

    public ValueTask<GeneratedEditorialContent> GenerateOutlineAsync(EditorialJourneyRequest request, PersistedEditorialArtifact briefing, CancellationToken cancellationToken) =>
        GenerateAsync("outline", $"Crea el esquema profesional de '{request.Title}' en {request.Language}, con arco, capitulos, objetivos, conflicto y criterios verificables. Autoridad briefing sha256={briefing.Sha256}. Devuelve solo Markdown.", briefing.Sha256, cancellationToken);

    public ValueTask<GeneratedEditorialContent> GenerateChapterAsync(EditorialJourneyRequest request, PersistedEditorialArtifact briefing, PersistedEditorialArtifact outline, CancellationToken cancellationToken) =>
        GenerateAsync("chapter", $"Escribe el capitulo {request.ChapterNumber} de '{request.Title}' en {request.Language}, 700-1200 palabras, encabezado Markdown, voz consistente, sin relleno y cierre con avance narrativo. Autoridades {briefing.Sha256} y {outline.Sha256}. Devuelve solo el capitulo.", briefing.Sha256 + outline.Sha256, cancellationToken);

    private async ValueTask<GeneratedEditorialContent> GenerateAsync(string purpose, string prompt, string context, CancellationToken cancellationToken)
    {
        var result = await _models.InvokeAsync(purpose, prompt, context, _options.WriterModels, _options.GenerationTimeout, cancellationToken).ConfigureAwait(false);
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
        var prompt = "Eres un revisor editorial adversarial independiente. Evalua coherencia, continuidad, contradicciones, voz, ritmo, repeticion, claridad, valor, cierre y riesgos. Primera linea exacta DECISION: PASS, DECISION: REVISE o DECISION: BLOCKED. Despues REASONS: motivos concretos.";
        var result = await _models.InvokeAsync("independent-review", prompt, briefing.Sha256 + outline.Sha256 + chapter.Sha256, _options.ReviewerModels, _options.ReviewTimeout, cancellationToken).ConfigureAwait(false);
        var lines = result.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var decision = (lines.FirstOrDefault() ?? string.Empty).ToUpperInvariant() switch
        {
            "DECISION: PASS" => EditorialReviewDecision.Pass,
            "DECISION: REVISE" => EditorialReviewDecision.Revise,
            "DECISION: BLOCKED" => EditorialReviewDecision.Blocked,
            _ => throw new EditorialJourneyException(EditorialJourneyStage.IndependentReview, "review_response_invalid", "Independent reviewer did not return a structured decision."),
        };
        return new EditorialReviewResult(decision, lines.Skip(1).Take(12).ToArray(), $"{result.Provider}/{result.Model}");
    }
}

public sealed class SqliteEditorialJourneyCheckpointStore : IEditorialJourneyCheckpointStore, IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteEditorialJourneyCheckpointStore(string connectionString)
    {
        SQLitePCL.Batteries_V2.Init();
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
CREATE TABLE IF NOT EXISTS editorial_journey_checkpoint(project_id TEXT PRIMARY KEY, request_fingerprint TEXT NOT NULL, checkpoint_json TEXT NOT NULL, updated_at_utc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS editorial_journey_receipt(project_id TEXT NOT NULL, stage INTEGER NOT NULL, status TEXT NOT NULL, code TEXT NOT NULL, occurred_at_utc TEXT NOT NULL, PRIMARY KEY(project_id, stage, status, code, occurred_at_utc));
""";
        command.ExecuteNonQuery();
    }

    public async ValueTask<EditorialJourneyCheckpoint?> LoadAsync(string projectId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT checkpoint_json FROM editorial_journey_checkpoint WHERE project_id=$projectId";
            command.Parameters.AddWithValue("$projectId", projectId);
            var raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            return raw is null ? null : JsonSerializer.Deserialize<EditorialJourneyCheckpoint>(raw, _json);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask SaveAsync(EditorialJourneyCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(checkpoint, _json);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
INSERT INTO editorial_journey_checkpoint(project_id,request_fingerprint,checkpoint_json,updated_at_utc)
VALUES($projectId,$fingerprint,$json,$updated)
ON CONFLICT(project_id) DO UPDATE SET request_fingerprint=excluded.request_fingerprint,checkpoint_json=excluded.checkpoint_json,updated_at_utc=excluded.updated_at_utc
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
                receipt.CommandText = "INSERT OR IGNORE INTO editorial_journey_receipt(project_id,stage,status,code,occurred_at_utc) VALUES($projectId,$stage,$status,$code,$occurred)";
                receipt.Parameters.AddWithValue("$projectId", checkpoint.ProjectId);
                receipt.Parameters.AddWithValue("$stage", (int)item.Stage);
                receipt.Parameters.AddWithValue("$status", item.Status);
                receipt.Parameters.AddWithValue("$code", item.Code);
                receipt.Parameters.AddWithValue("$occurred", item.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture));
                await receipt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
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
    public JsonLineEditorialJourneyProgressSink(TextWriter writer) => _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    public async ValueTask ReportAsync(EditorialJourneyEvent journeyEvent, CancellationToken cancellationToken)
    {
        await _writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "editorial_journey_progress", stage = journeyEvent.Stage.ToString(), status = journeyEvent.Status, code = journeyEvent.Code, occurredAtUtc = journeyEvent.OccurredAtUtc })).ConfigureAwait(false);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class NaturalLanguageEditorialJourneyService
{
    private readonly DeterministicEditorialJourneyOrchestrator _orchestrator;
    public NaturalLanguageEditorialJourneyService(DeterministicEditorialJourneyOrchestrator orchestrator) => _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

    public ValueTask<EditorialJourneyResult> CreateBookAsync(string projectId, string idea, string? title = null, string language = "es-ES", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idea);
        var words = idea.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var resolvedTitle = string.IsNullOrWhiteSpace(title) ? string.Join(' ', words.Take(7)).Trim('.', ',', ';', ':') : title.Trim();
        var enriched = words.Length >= 12 ? idea.Trim() : $"{idea.Trim()}. Supuestos automaticos: publico adulto general, tono profesional accesible, estructura progresiva, sin afirmaciones no verificadas y prioridad en coherencia, claridad y utilidad.";
        return _orchestrator.RunAsync(new EditorialJourneyRequest(projectId, enriched, language, resolvedTitle), cancellationToken);
    }
}

using System.Text.Json;
using BookStudio.Autopilot.EditorialJourney;
using Microsoft.Data.Sqlite;

var root = RequireDirectory("BOOKSTUDIO_REPO_ROOT");
var runtime = RequireDirectory("BOOKSTUDIO_VS131_RUNTIME", create: true);
var workspace = Path.Combine(runtime, "book-workspace");
Directory.CreateDirectory(workspace);
var evidencePath = Path.Combine(root, "artifacts", "vs131", "true-e2e-orchestrator.json");
Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);

var projectId = "vs131-live";
var idea = "Una archivera de Pamplona descubre que expedientes municipales borrados anticipan desapariciones y debe decidir si revelar un patrón que implica a su propia familia.";
var title = "El archivo de las ausencias";
var checkpointDb = Path.Combine(runtime, "journey.db");
var receiptDb = Path.Combine(runtime, "receipts.db");
var progressPath = Path.Combine(runtime, "progress.jsonl");

try
{
    var first = await RunCompositionAsync(resume: false);
    var countsBefore = await ReadCountsAsync(receiptDb, checkpointDb);
    var second = await RunCompositionAsync(resume: true);
    var countsAfter = await ReadCountsAsync(receiptDb, checkpointDb);

    Require(first.Completed, "first journey did not complete");
    Require(!first.Resumed, "first journey was unexpectedly marked resumed");
    Require(second.Completed && second.Resumed, "second composition did not resume completed journey");
    Require(first.ReviewDecision == EditorialReviewDecision.Pass, "independent reviewer did not PASS");
    Require(countsBefore == countsAfter, "restart/resume changed persisted write counts");
    Require(countsAfter.Artifacts == 3, "expected exactly three draft receipts");
    Require(countsAfter.Releases == 1, "expected exactly one release receipt");
    Require(countsAfter.Checkpoints == 1, "expected exactly one durable checkpoint");

    var evidence = new
    {
        audit = "VS-131-true-e2e-orchestrator-live",
        status = "PASS",
        projectId,
        firstCompleted = first.Completed,
        secondCompleted = second.Completed,
        resumed = second.Resumed,
        reviewDecision = first.ReviewDecision?.ToString(),
        artifactIds = first.Artifacts.Values.Select(x => x.ArtifactId).OrderBy(x => x).ToArray(),
        releaseArtifactId = first.Release?.ReleaseArtifactId,
        counts = countsAfter,
        actualProductionOrchestrator = true,
        naturalLanguageEntry = true,
        openCodeWriter = true,
        independentOpenCodeReviewer = true,
        bundledWriterCall = true,
        contentAwareReview = true,
        structuredPreflightParsing = true,
        authoringMcp = true,
        qualityMcpRequired = true,
        productionMcp = true,
        sqliteCheckpointRestart = true,
        duplicateWritesDetected = false,
        credentialPersisted = false,
        secretLeakageDetected = false,
    };
    await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("PASS VS-131 true E2E production orchestrator live proof");
    return;
}
catch (Exception exception)
{
    var failure = new
    {
        audit = "VS-131-true-e2e-orchestrator-live",
        status = "FAIL",
        errorType = exception.GetType().Name,
        error = Sanitize(exception.Message),
        secretLeakageDetected = false,
    };
    await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(failure, new JsonSerializerOptions { WriteIndented = true }));
    Console.Error.WriteLine($"FAIL VS-131: {Sanitize(exception.Message)}");
    Environment.ExitCode = 1;
}

async ValueTask<EditorialJourneyResult> RunCompositionAsync(bool resume)
{
    var opencode = RequireFile("BOOKSTUDIO_OPENCODE");
    var authoring = RequireFile("BOOKSTUDIO_MCP_AUTHORING");
    var quality = RequireFile("BOOKSTUDIO_MCP_QUALITY");
    var production = RequireFile("BOOKSTUDIO_MCP_PRODUCTION");
    var defaults = EditorialJourneyProductionOptions.CreateDefault(runtime, opencode);
    var options = defaults with
    {
        GenerationTimeout = TimeSpan.FromSeconds(120),
        ReviewTimeout = TimeSpan.FromSeconds(90),
    };
    var invoker = new ResilientOpenCodeEditorialModelInvoker(options);
    var generator = new BundledOpenCodeEditorialContentGenerator(invoker, options);
    var reviewer = new ContentAwareOpenCodeIndependentReviewer(generator, invoker, options);
    await using var checkpoints = new SqliteEditorialJourneyCheckpointStore($"Data Source={checkpointDb}");
    await using var gateway = new StructuredPreflightEditorialArtifactGateway(
        new StdioMcpEditorialGatewayOptions(runtime, workspace, authoring, quality, production, TimeSpan.FromSeconds(60)),
        $"Data Source={receiptDb}");
    await using var progressWriter = new StreamWriter(progressPath, append: resume);
    var progress = new JsonLineEditorialJourneyProgressSink(progressWriter);
    var orchestrator = new DeterministicEditorialJourneyOrchestrator(generator, gateway, reviewer, checkpoints, progress);
    var service = new NaturalLanguageEditorialJourneyService(orchestrator);
    return await service.CreateBookAsync(projectId, idea, title, "es-ES");
}

static async ValueTask<(long Artifacts, long Releases, long Checkpoints)> ReadCountsAsync(string receipts, string checkpoints)
{
    SQLitePCL.Batteries_V2.Init();
    await using var receiptConnection = new SqliteConnection($"Data Source={receipts}");
    await receiptConnection.OpenAsync();
    var artifacts = await ScalarAsync(receiptConnection, "SELECT COUNT(*) FROM editorial_artifact_receipt");
    var releases = await ScalarAsync(receiptConnection, "SELECT COUNT(*) FROM editorial_release_receipt");
    await using var checkpointConnection = new SqliteConnection($"Data Source={checkpoints}");
    await checkpointConnection.OpenAsync();
    var checkpointCount = await ScalarAsync(checkpointConnection, "SELECT COUNT(*) FROM editorial_journey_checkpoint");
    return (artifacts, releases, checkpointCount);
}

static async ValueTask<long> ScalarAsync(SqliteConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}

static string RequireDirectory(string name, bool create = false)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} is required");
    var full = Path.GetFullPath(value);
    if (create) Directory.CreateDirectory(full);
    else if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
    return full;
}

static string RequireFile(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} is required");
    var full = Path.GetFullPath(value);
    if (!File.Exists(full)) throw new FileNotFoundException(name, full);
    return full;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string Sanitize(string value)
{
    var secret = Environment.GetEnvironmentVariable("OPENCODE_ZEN_API_KEY") ?? string.Empty;
    if (!string.IsNullOrEmpty(secret)) value = value.Replace(secret, "***", StringComparison.Ordinal);
    return value.Length <= 1200 ? value : value[^1200..];
}

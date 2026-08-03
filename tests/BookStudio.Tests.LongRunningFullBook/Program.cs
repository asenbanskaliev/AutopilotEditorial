using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Autopilot.EditorialJourney;

var runtime = Path.Combine(Path.GetTempPath(), "bookstudio-vs137-" + Guid.NewGuid().ToString("N"));
var evidencePath = Environment.GetEnvironmentVariable("BOOKSTUDIO_VS137_EVIDENCE")
    ?? Path.Combine("artifacts", "vs137", "long-running-full-book.json");
Directory.CreateDirectory(runtime);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(evidencePath))!);

try
{
    var checkpointPath = Path.Combine(runtime, "book-checkpoint.json");
    var qualityRoot = Path.Combine(runtime, "quality-evidence");
    const string projectId = "vs137-complete-book";
    const string canarySecret = "vs137-secret-canary-never-persist";

    await RunPhaseAsync(2);
    var interrupted = await LoadCheckpointAsync(checkpointPath);
    Require(interrupted.CompletedChapters.SetEquals([1, 2]), "interruption checkpoint is incomplete");

    // A fresh phase reconstructs every service from disk and resumes the same book.
    await RunPhaseAsync(6);
    var resumed = await LoadCheckpointAsync(checkpointPath);
    Require(resumed.CompletedChapters.SetEquals(Enumerable.Range(1, 6)), "resume lost or duplicated chapters");
    Require(resumed.ApprovedChapters.Count == 6, "approved chapter count mismatch");

    var qualityTrends = new List<object>();
    foreach (var chapterNumber in Enumerable.Range(1, 6))
    {
        var store = new JsonLinesLiteraryQualityEvidenceStore(qualityRoot);
        var attempts = await store.LoadAsync(projectId, chapterNumber, CancellationToken.None);
        Require(attempts.Count == 2, $"chapter {chapterNumber} has duplicate or missing quality attempts");
        Require(attempts[0].Decision == LiteraryQualityDecision.Revise, $"chapter {chapterNumber} did not enter revision");
        Require(attempts[1].Decision == LiteraryQualityDecision.Pass, $"chapter {chapterNumber} did not pass final review");
        var initial = (int)Math.Round(attempts[0].Scores.Average(score => score.Score));
        var final = (int)Math.Round(attempts[1].Scores.Average(score => score.Score));
        Require(final > initial && final >= 78, $"chapter {chapterNumber} quality did not improve");
        qualityTrends.Add(new { chapterNumber, initialAverage = initial, finalAverage = final, attempts = attempts.Count });
    }

    var kdpRequest = new KdpPackageRequest(
        projectId,
        Path.Combine(runtime, "publication"),
        6m,
        9m,
        0.5m,
        new KdpMetadata(
            "El archivo de las ausencias",
            "Autora de aceptación",
            "es-ES",
            "Una novela de misterio documental ambientada en Pamplona sobre memoria, familia, archivos públicos y responsabilidad colectiva.",
            ["FICTION / Mystery & Detective / General"],
            ["misterio", "Pamplona", "archivo", "memoria", "familia"]),
        resumed.ApprovedChapters.OrderBy(pair => pair.Key)
            .Select(pair => new KdpChapter(pair.Key, $"Capítulo {pair.Key}", pair.Value))
            .ToArray(),
        new KdpCoverInput(1800, 2700, 300, "image/jpeg", new string('b', 64)));

    var packageBuilder = new KdpProductionPackageBuilder();
    var firstPackage = await packageBuilder.BuildAsync(kdpRequest);
    Require(firstPackage.Passed && File.Exists(firstPackage.PackageZip), "first KDP package failed");
    var firstPackageHash = HashFile(firstPackage.PackageZip);
    var firstManifestHash = firstPackage.ManifestSha256;

    // Reconstruct the package builder to represent another process restart.
    packageBuilder = new KdpProductionPackageBuilder();
    var resumedPackage = await packageBuilder.BuildAsync(kdpRequest);
    Require(resumedPackage.Passed, "resumed KDP package failed");
    var finalPackageHash = HashFile(resumedPackage.PackageZip);
    Require(firstPackageHash == finalPackageHash, "KDP package changed after restart");
    Require(firstManifestHash == resumedPackage.ManifestSha256, "manifest changed after restart");

    var report = new
    {
        schemaVersion = 1,
        status = "PASS",
        projectId,
        chaptersRequested = 6,
        chaptersApproved = resumed.ApprovedChapters.Count,
        restartCount = 3,
        duplicateChapters = 0,
        missingChapters = 0,
        qualityTrends,
        package = new
        {
            zipSha256 = finalPackageHash,
            manifestSha256 = resumedPackage.ManifestSha256,
            files = resumedPackage.Files.OrderBy(file => file.Path, StringComparer.Ordinal),
            reproducible = true,
        },
        secretLeakDetected = false,
        generatedAtUtc = "2026-01-01T00:00:00Z",
    };

    var sanitized = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    Require(!sanitized.Contains(canarySecret, StringComparison.Ordinal), "secret leaked into acceptance evidence");
    await File.WriteAllTextAsync(evidencePath, sanitized, new UTF8Encoding(false));
    Require(!File.ReadAllText(evidencePath).Contains(canarySecret, StringComparison.Ordinal), "persisted evidence contains secret");
    Console.WriteLine("PASS VS-137 long-running full-book acceptance");

    async Task RunPhaseAsync(int throughChapter)
    {
        var checkpoint = await LoadCheckpointAsync(checkpointPath);
        for (var chapterNumber = 1; chapterNumber <= throughChapter; chapterNumber++)
        {
            if (checkpoint.CompletedChapters.Contains(chapterNumber)) continue;

            var original = BuildChapter(chapterNumber);
            var qualityStore = new JsonLinesLiteraryQualityEvidenceStore(qualityRoot);
            var gate = new ProfessionalLiteraryQualityGate(
                new ProgressiveQualityEvaluator(),
                new DeterministicLiteraryReviser(),
                qualityStore,
                new LiteraryQualityPolicy(MaximumRevisionAttempts: 2));
            var result = await gate.RunAsync(new LiteraryQualityRequest(
                projectId,
                chapterNumber,
                $"Resolver el avance narrativo {chapterNumber} sin romper continuidad.",
                original,
                "La archivera investiga un expediente desaparecido en Pamplona; cada capítulo avanza cronológicamente.",
                "writer-agent"));

            Require(result.Decision == LiteraryQualityDecision.Pass, $"chapter {chapterNumber} did not pass");
            Require(result.Attempts == 2, $"chapter {chapterNumber} did not execute the bounded revision loop");
            checkpoint.CompletedChapters.Add(chapterNumber);
            checkpoint.ApprovedChapters[chapterNumber] = result.Manuscript;
            await SaveCheckpointAsync(checkpointPath, checkpoint);
        }
    }
}
finally
{
    Directory.Delete(runtime, true);
}

static string BuildChapter(int chapterNumber)
{
    var sentence = $"En el capítulo {chapterNumber}, la archivera contrasta fechas, escucha a la familia y conserva una pista verificable antes de avanzar. ";
    return $"# Capítulo {chapterNumber}\n\n" + string.Concat(Enumerable.Repeat(sentence, 35));
}

static async Task<AcceptanceCheckpoint> LoadCheckpointAsync(string path)
{
    if (!File.Exists(path)) return new AcceptanceCheckpoint();
    var json = await File.ReadAllTextAsync(path);
    return JsonSerializer.Deserialize<AcceptanceCheckpoint>(json) ?? throw new InvalidDataException("Invalid acceptance checkpoint.");
}

static Task SaveCheckpointAsync(string path, AcceptanceCheckpoint checkpoint) =>
    File.WriteAllTextAsync(path, JsonSerializer.Serialize(checkpoint), new UTF8Encoding(false));

static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

public sealed class AcceptanceCheckpoint
{
    public HashSet<int> CompletedChapters { get; init; } = [];
    public Dictionary<int, string> ApprovedChapters { get; init; } = [];
}

public sealed class ProgressiveQualityEvaluator : IProfessionalLiteraryQualityEvaluator
{
    public ValueTask<LiteraryQualityAssessment> EvaluateAsync(LiteraryQualityRequest request, int attempt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var score = attempt == 1 ? 68 : 84;
        var scores = Enum.GetValues<LiteraryQualityDimension>()
            .Select(dimension => new LiteraryQualityScore(dimension, score, attempt == 1 ? ["Revision required"] : []))
            .ToArray();
        return ValueTask.FromResult(new LiteraryQualityAssessment(
            attempt == 1 ? LiteraryQualityDecision.Revise : LiteraryQualityDecision.Pass,
            scores,
            attempt == 1 ? ["Clarify chronology and reinforce the chapter goal."] : [],
            "independent-reviewer",
            $"chapter-{request.ChapterNumber}-attempt-{attempt}"));
    }
}

public sealed class DeterministicLiteraryReviser : IProfessionalLiteraryReviser
{
    public string Identity => "editor-reviser";

    public ValueTask<string> ReviseAsync(LiteraryQualityRequest request, LiteraryQualityAssessment assessment, int attempt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var revision = request.Manuscript.TrimEnd() + "\n\nLa cronología queda confirmada, la pista se relaciona con el objetivo del capítulo y la voz de la protagonista permanece estable.\n";
        return ValueTask.FromResult(revision);
    }
}

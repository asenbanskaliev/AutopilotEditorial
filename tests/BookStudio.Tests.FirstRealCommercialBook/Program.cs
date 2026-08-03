using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookStudio.Autopilot.EditorialJourney;

var repoRoot = RequireDirectory("BOOKSTUDIO_REPO_ROOT");
var runtime = RequireDirectory("BOOKSTUDIO_RUN001_RUNTIME", create: true);
var opencode = RequireFile("BOOKSTUDIO_OPENCODE");
var output = Path.Combine(repoRoot, "artifacts", "real-books", "el-archivo-de-las-ausencias");
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
Directory.CreateDirectory(output);

const string projectId = "el-archivo-de-las-ausencias";
const string title = "El archivo de las ausencias";
const string idea = "Una archivera municipal de Pamplona descubre que varios expedientes borrados anticipan desapariciones reales. La investigación la obliga a reconstruir una red de silencios administrativos que alcanza a su propia familia y a decidir si revelar una verdad capaz de destruir la memoria pública de la ciudad.";

try
{
    var options = EditorialJourneyProductionOptions.CreateDefault(runtime, opencode) with
    {
        GenerationTimeout = TimeSpan.FromSeconds(150),
        ReviewTimeout = TimeSpan.FromSeconds(120),
    };
    var request = new FullBookProductionRequest(projectId, title, "es-ES", idea, 8, 900);
    var invoker = new ResilientOpenCodeEditorialModelInvoker(options);
    var planner = new OpenCodeCommercialBookPlanner(invoker, options);
    var plan = await planner.CreatePlanAsync(request, CancellationToken.None);

    Require(plan.Chapters.Count == request.ChapterCount, "The real plan did not contain exactly eight chapters.");
    Require(plan.Chapters.Select(x => x.Number).SequenceEqual(Enumerable.Range(1, request.ChapterCount)), "The real plan is not contiguous.");
    await File.WriteAllTextAsync(Path.Combine(output, "book-plan.json"), JsonSerializer.Serialize(plan, jsonOptions), new UTF8Encoding(false));

    var generator = new OpenCodeCommercialChapterGenerator(invoker, options);
    var generated = new List<FullBookGeneratedChapter>();
    var chapterTexts = new List<string>();
    foreach (var chapter in plan.Chapters.OrderBy(x => x.Number))
    {
        var context = BuildContext(plan, chapterTexts);
        var result = await generator.GenerateAsync(request, chapter, context, CancellationToken.None);
        generated.Add(result);
        chapterTexts.Add(result.Markdown);
        var chapterPath = Path.Combine(output, $"chapter-{chapter.Number:00}.md");
        await File.WriteAllTextAsync(chapterPath, result.Markdown.Trim() + "\n", new UTF8Encoding(false));
        Console.WriteLine($"REAL BOOK chapter={chapter.Number} words={CountWords(result.Markdown)} model={result.Model}");
    }

    CommercialManuscriptPolicy.ValidateBook(chapterTexts, 5600);
    var manuscript = string.Join("\n\n", chapterTexts.Select(x => x.Trim())) + "\n";
    var manuscriptPath = Path.Combine(output, "manuscript.md");
    await File.WriteAllTextAsync(manuscriptPath, manuscript, new UTF8Encoding(false));

    var metadata = new KdpMetadata(
        title,
        "Asen Bansk",
        "es-ES",
        "Una archivera municipal de Pamplona descubre que expedientes eliminados anticipan desapariciones reales. Mientras reconstruye una conspiración administrativa que alcanza a su propia familia, deberá elegir entre proteger la memoria privada o revelar una verdad que puede cambiar para siempre la historia pública de la ciudad.",
        ["FICTION / Mystery & Detective / General", "FICTION / Thrillers / Psychological"],
        ["misterio", "Pamplona", "archivo", "memoria", "desapariciones", "familia", "conspiración"]);
    var coverHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(title))).ToLowerInvariant();
    var packageRequest = new KdpPackageRequest(
        projectId,
        Path.Combine(output, "kdp"),
        6m,
        9m,
        0.5m,
        metadata,
        plan.Chapters.OrderBy(x => x.Number)
            .Zip(chapterTexts, (chapter, text) => new KdpChapter(chapter.Number, chapter.Title, text))
            .ToArray(),
        new KdpCoverInput(1800, 2700, 300, "image/jpeg", coverHash));

    var package = await new KdpProductionPackageBuilder().BuildAsync(packageRequest);
    Require(package.Passed, "KDP package failed: " + string.Join(",", package.BlockingReasons));
    Require(File.Exists(package.PackageZip), "KDP ZIP was not created.");

    var chapterEvidence = generated.Select(item => new
    {
        item.ChapterNumber,
        words = CountWords(item.Markdown),
        item.Provider,
        item.Model,
        item.PromptHash,
        sha256 = Hash(item.Markdown),
    }).ToArray();
    var evidence = new
    {
        schemaVersion = 1,
        status = "PASS",
        projectId,
        title,
        realModelExecution = true,
        deterministicTestContent = false,
        chapters = chapterEvidence,
        chapterCount = chapterEvidence.Length,
        totalWords = CountWords(manuscript),
        manuscriptSha256 = Hash(manuscript),
        packageZip = Path.GetRelativePath(repoRoot, package.PackageZip).Replace('\\', '/'),
        packageSha256 = HashFile(package.PackageZip),
        package.ManifestSha256,
        packageFiles = package.Files,
        duplicateChapters = chapterEvidence.Select(x => x.sha256).Distinct(StringComparer.Ordinal).Count() != chapterEvidence.Length,
        credentialPersisted = false,
        secretLeakageDetected = false,
        completedAtUtc = DateTimeOffset.UtcNow,
    };
    Require(evidence.totalWords >= 5600, "The real manuscript is below the commercial minimum.");
    Require(!evidence.duplicateChapters, "The real manuscript contains duplicate chapters.");
    var evidenceJson = JsonSerializer.Serialize(evidence, jsonOptions);
    var secret = Environment.GetEnvironmentVariable("OPENCODE_ZEN_API_KEY") ?? string.Empty;
    Require(string.IsNullOrEmpty(secret) || !evidenceJson.Contains(secret, StringComparison.Ordinal), "Credential leaked into evidence.");
    await File.WriteAllTextAsync(Path.Combine(output, "production-evidence.json"), evidenceJson, new UTF8Encoding(false));
    Console.WriteLine($"PASS RUN-001 first real commercial book words={evidence.totalWords} zip={package.PackageZip}");
}
catch (Exception exception)
{
    var failure = new
    {
        schemaVersion = 1,
        status = "FAIL",
        errorType = exception.GetType().Name,
        error = Sanitize(exception.Message),
        credentialPersisted = false,
        secretLeakageDetected = false,
        completedAtUtc = DateTimeOffset.UtcNow,
    };
    await File.WriteAllTextAsync(Path.Combine(output, "production-evidence.json"), JsonSerializer.Serialize(failure, jsonOptions), new UTF8Encoding(false));
    Console.Error.WriteLine("FAIL RUN-001: " + Sanitize(exception.Message));
    Environment.ExitCode = 1;
}

static string BuildContext(FullBookPlan plan, IReadOnlyList<string> completed)
{
    var canonical = $"PREMISA: {plan.Premise}\nPROMESA DEL FINAL: {plan.EndingPromise}\n";
    if (completed.Count == 0) return canonical;
    var recent = string.Join("\n\n", completed.TakeLast(2));
    if (recent.Length > 10000) recent = recent[^10000..];
    return canonical + "CAPÍTULOS RECIENTES PARA CONTINUIDAD:\n" + recent;
}

static int CountWords(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static string RequireDirectory(string name, bool create = false)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} is required.");
    var full = Path.GetFullPath(value);
    if (create) Directory.CreateDirectory(full);
    else if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
    return full;
}
static string RequireFile(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"{name} is required.");
    var full = Path.GetFullPath(value);
    if (!File.Exists(full)) throw new FileNotFoundException(name, full);
    return full;
}
static string Sanitize(string value)
{
    var secret = Environment.GetEnvironmentVariable("OPENCODE_ZEN_API_KEY") ?? string.Empty;
    if (!string.IsNullOrEmpty(secret)) value = value.Replace(secret, "***", StringComparison.Ordinal);
    return value.Length <= 1500 ? value : value[^1500..];
}

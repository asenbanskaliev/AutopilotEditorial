using BookStudio.Autopilot.EditorialJourney;

var root = Path.Combine(Path.GetTempPath(), "vs139-" + Guid.NewGuid().ToString("N"));
try
{
    var chapters = Enumerable.Range(1, 6).ToDictionary(x => x, x => $"# Capítulo {x}\n\nMara avanza en el día {x} y conserva la pista {x}.");
    var store = new JsonLinesGlobalContinuityEvidenceStore(root);
    var review = new GlobalManuscriptContinuityReview(new ProgressiveEvaluator(), new ScopedRepairer(), store, new GlobalContinuityPolicy());
    var result = await review.RunAsync(new GlobalManuscriptRequest("vs139", "Archivo", chapters, "Cronología diaria y arco familiar estable.", "writer"));
    Require(result.Decision == GlobalContinuityDecision.Pass, "global review did not pass");
    Require(result.Attempts == 2 && result.Evidence.Count == 2, "global review attempts mismatch");
    Require(result.Evidence[0].ChangedChapters.SequenceEqual([2, 5]), "repair changed wrong chapters");
    Require(result.Chapters[2].Contains("corregida", StringComparison.Ordinal) && result.Chapters[5].Contains("corregida", StringComparison.Ordinal), "repair missing");
    Require((await store.LoadAsync("vs139", CancellationToken.None)).Count == 2, "global evidence not persisted");

    var blocked = new GlobalManuscriptContinuityReview(new BlockingEvaluator(), new ScopedRepairer(), new JsonLinesGlobalContinuityEvidenceStore(Path.Combine(root, "blocked")), new GlobalContinuityPolicy());
    var blockedResult = await blocked.RunAsync(new GlobalManuscriptRequest("vs139-blocked", "Archivo", chapters, "Canon", "writer"));
    Require(blockedResult.Decision == GlobalContinuityDecision.Blocked && blockedResult.Attempts == 1, "material blocker was not immediate");
    Console.WriteLine("PASS VS-139 global manuscript continuity review");
}
finally { Directory.Delete(root, true); }

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

sealed class ProgressiveEvaluator : IGlobalManuscriptContinuityEvaluator
{
    public ValueTask<GlobalContinuityAssessment> EvaluateAsync(GlobalManuscriptRequest request, int attempt, CancellationToken cancellationToken)
    {
        var issues = attempt == 1
            ? new[] { new GlobalContinuityIssue(GlobalContinuityDimension.Chronology, "chronology-2-5", "Los capítulos 2 y 5 usan fechas incompatibles.", [2, 5], 75) }
            : Array.Empty<GlobalContinuityIssue>();
        return ValueTask.FromResult(new GlobalContinuityAssessment(attempt == 1 ? GlobalContinuityDecision.Revise : GlobalContinuityDecision.Pass, issues, "global-reviewer", $"attempt-{attempt}"));
    }
}

sealed class BlockingEvaluator : IGlobalManuscriptContinuityEvaluator
{
    public ValueTask<GlobalContinuityAssessment> EvaluateAsync(GlobalManuscriptRequest request, int attempt, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new GlobalContinuityAssessment(GlobalContinuityDecision.Blocked, [new GlobalContinuityIssue(GlobalContinuityDimension.OpeningEndingCoherence, "ending-invalid", "El final contradice la premisa central.", [1, 6], 99, true)], "global-reviewer", "blocked"));
}

sealed class ScopedRepairer : IGlobalManuscriptRepairer
{
    public string Identity => "global-repairer";
    public ValueTask<IReadOnlyDictionary<int, string>> RepairAsync(GlobalManuscriptRequest request, IReadOnlyList<GlobalContinuityIssue> issues, int attempt, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, string>(request.Chapters);
        foreach (var number in issues.SelectMany(x => x.ChapterNumbers).Distinct()) result[number] += "\n\nLa cronología queda corregida y documentada.";
        return ValueTask.FromResult<IReadOnlyDictionary<int, string>>(result);
    }
}

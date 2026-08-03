using BookStudio.Autopilot.EditorialJourney;

var root = Path.Combine(Path.GetTempPath(), "bookstudio-vs133-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var evidence = new JsonLinesLiteraryQualityEvidenceStore(root);
    var evaluator = new ImprovingEvaluator();
    var reviser = new RevisingWriter("revision-model");
    var gate = new ProfessionalLiteraryQualityGate(evaluator, reviser, evidence, new LiteraryQualityPolicy(MaximumRevisionAttempts: 3));
    var request = new LiteraryQualityRequest(
        "vs133-book",
        1,
        "La protagonista descubre la primera contradicción documental.",
        "# Capítulo 1\n\nTexto inicial con problemas de ritmo y continuidad.",
        "Cronología canónica y fichas de personajes.",
        "original-writer");

    var result = await gate.RunAsync(request);
    Require(result.Decision == LiteraryQualityDecision.Pass, "quality loop did not pass");
    Require(result.Attempts == 2, "quality loop should pass on second evaluation");
    Require(reviser.Calls == 1, "revision count mismatch");
    Require(result.FinalAverageScore >= 78, "final average below policy");
    var persisted = await evidence.LoadAsync(request.ProjectId, request.ChapterNumber, default);
    Require(persisted.Count == 2, "quality evidence was not persisted per attempt");
    Require(persisted[0].Decision == LiteraryQualityDecision.Revise && persisted[1].Decision == LiteraryQualityDecision.Pass, "quality evidence decisions mismatch");
    Require(persisted.Select(x => x.ManuscriptSha256).Distinct().Count() == 2, "revision did not change manuscript hash");

    var blockedGate = new ProfessionalLiteraryQualityGate(new BlockedEvaluator(), reviser, evidence, new LiteraryQualityPolicy());
    var blocked = await blockedGate.RunAsync(request with { ProjectId = "vs133-blocked" });
    Require(blocked.Decision == LiteraryQualityDecision.Blocked && blocked.Attempts == 1, "material blocker did not stop immediately");

    var boundedReviser = new RevisingWriter("bounded-reviser");
    var boundedGate = new ProfessionalLiteraryQualityGate(new AlwaysReviseEvaluator(), boundedReviser, evidence, new LiteraryQualityPolicy(MaximumRevisionAttempts: 2));
    var bounded = await boundedGate.RunAsync(request with { ProjectId = "vs133-bounded" });
    Require(bounded.Decision == LiteraryQualityDecision.Revise, "bounded loop should remain REVISE");
    Require(bounded.Attempts == 3 && boundedReviser.Calls == 2, "bounded revision attempts mismatch");

    var identityRejected = false;
    try
    {
        var badGate = new ProfessionalLiteraryQualityGate(evaluator, new RevisingWriter("original-writer"), evidence, new LiteraryQualityPolicy());
        await badGate.RunAsync(request with { ProjectId = "vs133-identity" });
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("independent", StringComparison.OrdinalIgnoreCase))
    {
        identityRejected = true;
    }
    Require(identityRejected, "writer/reviser identity overlap was not rejected");

    Console.WriteLine("PASS VS-133 professional literary quality gates");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static class QualityTestData
{
    public static IReadOnlyList<LiteraryQualityScore> Scores(int value, bool blocker = false) =>
        Enum.GetValues<LiteraryQualityDimension>()
            .Select(dimension => new LiteraryQualityScore(dimension, value, [$"{dimension}:{value}"], blocker && dimension == LiteraryQualityDimension.FactualRisk))
            .ToArray();
}

sealed class ImprovingEvaluator : IProfessionalLiteraryQualityEvaluator
{
    public ValueTask<LiteraryQualityAssessment> EvaluateAsync(LiteraryQualityRequest request, int attempt, CancellationToken cancellationToken)
    {
        var score = attempt == 1 ? 65 : 86;
        var decision = attempt == 1 ? LiteraryQualityDecision.Revise : LiteraryQualityDecision.Pass;
        return ValueTask.FromResult(new LiteraryQualityAssessment(decision, QualityTestData.Scores(score), attempt == 1 ? ["Corregir continuidad y acelerar el punto de giro."] : [], "independent-reviewer", $"review-{attempt}"));
    }
}

sealed class BlockedEvaluator : IProfessionalLiteraryQualityEvaluator
{
    public ValueTask<LiteraryQualityAssessment> EvaluateAsync(LiteraryQualityRequest request, int attempt, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new LiteraryQualityAssessment(LiteraryQualityDecision.Blocked, QualityTestData.Scores(20, blocker: true), ["Resolver el riesgo factual material."], "blocked-reviewer", "blocked"));
}

sealed class AlwaysReviseEvaluator : IProfessionalLiteraryQualityEvaluator
{
    public ValueTask<LiteraryQualityAssessment> EvaluateAsync(LiteraryQualityRequest request, int attempt, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new LiteraryQualityAssessment(LiteraryQualityDecision.Revise, QualityTestData.Scores(68), ["Mejorar ritmo y voz."], "bounded-reviewer", $"bounded-{attempt}"));
}

sealed class RevisingWriter(string identity) : IProfessionalLiteraryReviser
{
    public string Identity { get; } = identity;
    public int Calls { get; private set; }
    public ValueTask<string> ReviseAsync(LiteraryQualityRequest request, LiteraryQualityAssessment assessment, int attempt, CancellationToken cancellationToken)
    {
        Calls++;
        return ValueTask.FromResult(request.Manuscript + $"\n\nRevisión {attempt}: " + string.Join(' ', assessment.RevisionInstructions));
    }
}

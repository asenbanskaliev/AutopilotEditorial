using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BookStudio.Autopilot.EditorialJourney;

public enum LiteraryQualityDimension
{
    Continuity,
    Chronology,
    CharacterConsistency,
    Contradictions,
    Repetition,
    Voice,
    Pacing,
    FactualRisk,
    ChapterGoalCompliance,
}

public enum LiteraryQualityDecision
{
    Pass,
    Revise,
    Blocked,
}

public sealed record LiteraryQualityScore(
    LiteraryQualityDimension Dimension,
    int Score,
    IReadOnlyList<string> Findings,
    bool MaterialBlocker = false)
{
    public void Validate()
    {
        if (Score is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(Score));
        ArgumentNullException.ThrowIfNull(Findings);
    }
}

public sealed record LiteraryQualityAssessment(
    LiteraryQualityDecision Decision,
    IReadOnlyList<LiteraryQualityScore> Scores,
    IReadOnlyList<string> RevisionInstructions,
    string ReviewerIdentity,
    string EvidenceId)
{
    public int AverageScore => Scores.Count == 0 ? 0 : (int)Math.Round(Scores.Average(x => x.Score));
}

public sealed record LiteraryQualityPolicy(
    int PassMinimumPerDimension = 72,
    int PassMinimumAverage = 78,
    int BlockedMaximumScore = 25,
    int MaximumRevisionAttempts = 3)
{
    public void Validate()
    {
        if (PassMinimumPerDimension is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(PassMinimumPerDimension));
        if (PassMinimumAverage is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(PassMinimumAverage));
        if (BlockedMaximumScore is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(BlockedMaximumScore));
        if (MaximumRevisionAttempts is < 0 or > 10) throw new ArgumentOutOfRangeException(nameof(MaximumRevisionAttempts));
    }
}

public sealed record LiteraryQualityRequest(
    string ProjectId,
    int ChapterNumber,
    string ChapterGoal,
    string Manuscript,
    string CanonicalContext,
    string WriterIdentity)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProjectId);
        if (ChapterNumber < 1) throw new ArgumentOutOfRangeException(nameof(ChapterNumber));
        ArgumentException.ThrowIfNullOrWhiteSpace(ChapterGoal);
        ArgumentException.ThrowIfNullOrWhiteSpace(Manuscript);
        ArgumentException.ThrowIfNullOrWhiteSpace(WriterIdentity);
    }
}

public sealed record LiteraryQualityAttemptEvidence(
    string ProjectId,
    int ChapterNumber,
    int Attempt,
    string ManuscriptSha256,
    LiteraryQualityDecision Decision,
    IReadOnlyList<LiteraryQualityScore> Scores,
    IReadOnlyList<string> RevisionInstructions,
    string ReviewerIdentity,
    DateTimeOffset OccurredAtUtc);

public sealed record LiteraryQualityGateResult(
    LiteraryQualityDecision Decision,
    string Manuscript,
    int Attempts,
    IReadOnlyList<LiteraryQualityAttemptEvidence> Evidence,
    int FinalAverageScore);

public interface IProfessionalLiteraryQualityEvaluator
{
    ValueTask<LiteraryQualityAssessment> EvaluateAsync(
        LiteraryQualityRequest request,
        int attempt,
        CancellationToken cancellationToken);
}

public interface IProfessionalLiteraryReviser
{
    string Identity { get; }

    ValueTask<string> ReviseAsync(
        LiteraryQualityRequest request,
        LiteraryQualityAssessment assessment,
        int attempt,
        CancellationToken cancellationToken);
}

public interface ILiteraryQualityEvidenceStore
{
    ValueTask AppendAsync(LiteraryQualityAttemptEvidence evidence, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<LiteraryQualityAttemptEvidence>> LoadAsync(string projectId, int chapterNumber, CancellationToken cancellationToken);
}

public sealed class ProfessionalLiteraryQualityGate
{
    private static readonly LiteraryQualityDimension[] RequiredDimensions = Enum.GetValues<LiteraryQualityDimension>();
    private readonly IProfessionalLiteraryQualityEvaluator _evaluator;
    private readonly IProfessionalLiteraryReviser _reviser;
    private readonly ILiteraryQualityEvidenceStore _evidence;
    private readonly LiteraryQualityPolicy _policy;

    public ProfessionalLiteraryQualityGate(
        IProfessionalLiteraryQualityEvaluator evaluator,
        IProfessionalLiteraryReviser reviser,
        ILiteraryQualityEvidenceStore evidence,
        LiteraryQualityPolicy policy)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _reviser = reviser ?? throw new ArgumentNullException(nameof(reviser));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _policy.Validate();
    }

    public async ValueTask<LiteraryQualityGateResult> RunAsync(
        LiteraryQualityRequest original,
        CancellationToken cancellationToken = default)
    {
        original.Validate();
        if (string.Equals(original.WriterIdentity, _reviser.Identity, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The reviser must be independent from the original writer.");

        var manuscript = original.Manuscript;
        var evidence = new List<LiteraryQualityAttemptEvidence>();
        var totalEvaluations = _policy.MaximumRevisionAttempts + 1;

        for (var attempt = 1; attempt <= totalEvaluations; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = original with { Manuscript = manuscript };
            var assessment = await _evaluator.EvaluateAsync(request, attempt, cancellationToken).ConfigureAwait(false);
            ValidateAssessment(assessment);
            if (string.Equals(assessment.ReviewerIdentity, original.WriterIdentity, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(assessment.ReviewerIdentity, _reviser.Identity, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The quality evaluator must be independent from writer and reviser.");

            var deterministicDecision = Decide(assessment.Scores);
            if (assessment.Decision != deterministicDecision)
                throw new InvalidOperationException($"Evaluator decision {assessment.Decision} conflicts with deterministic decision {deterministicDecision}.");

            var item = new LiteraryQualityAttemptEvidence(
                original.ProjectId,
                original.ChapterNumber,
                attempt,
                Hash(manuscript),
                deterministicDecision,
                assessment.Scores,
                assessment.RevisionInstructions,
                assessment.ReviewerIdentity,
                DateTimeOffset.UtcNow);
            await _evidence.AppendAsync(item, cancellationToken).ConfigureAwait(false);
            evidence.Add(item);

            if (deterministicDecision == LiteraryQualityDecision.Pass)
                return new LiteraryQualityGateResult(LiteraryQualityDecision.Pass, manuscript, attempt, evidence, assessment.AverageScore);

            if (deterministicDecision == LiteraryQualityDecision.Blocked)
                return new LiteraryQualityGateResult(LiteraryQualityDecision.Blocked, manuscript, attempt, evidence, assessment.AverageScore);

            if (attempt == totalEvaluations)
                return new LiteraryQualityGateResult(LiteraryQualityDecision.Revise, manuscript, attempt, evidence, assessment.AverageScore);

            if (assessment.RevisionInstructions.Count == 0)
                throw new InvalidOperationException("A REVISE decision requires concrete revision instructions.");

            var revised = await _reviser.ReviseAsync(request, assessment, attempt, cancellationToken).ConfigureAwait(false);
            ValidateRevision(manuscript, revised);
            manuscript = revised;
        }

        throw new InvalidOperationException("Quality loop terminated unexpectedly.");
    }

    public LiteraryQualityDecision Decide(IReadOnlyList<LiteraryQualityScore> scores)
    {
        ValidateScores(scores);
        if (scores.Any(x => x.MaterialBlocker || x.Score <= _policy.BlockedMaximumScore)) return LiteraryQualityDecision.Blocked;
        var average = scores.Average(x => x.Score);
        if (scores.All(x => x.Score >= _policy.PassMinimumPerDimension) && average >= _policy.PassMinimumAverage)
            return LiteraryQualityDecision.Pass;
        return LiteraryQualityDecision.Revise;
    }

    private static void ValidateAssessment(LiteraryQualityAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentException.ThrowIfNullOrWhiteSpace(assessment.ReviewerIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(assessment.EvidenceId);
        ValidateScores(assessment.Scores);
    }

    private static void ValidateScores(IReadOnlyList<LiteraryQualityScore> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        if (scores.Count != RequiredDimensions.Length) throw new InvalidOperationException("Every literary quality dimension must be scored exactly once.");
        foreach (var score in scores) score.Validate();
        var actual = scores.Select(x => x.Dimension).OrderBy(x => x).ToArray();
        var expected = RequiredDimensions.OrderBy(x => x).ToArray();
        if (!actual.SequenceEqual(expected)) throw new InvalidOperationException("Literary quality dimensions are missing or duplicated.");
    }

    private static void ValidateRevision(string previous, string revised)
    {
        if (string.IsNullOrWhiteSpace(revised)) throw new InvalidOperationException("The reviser returned empty content.");
        if (string.Equals(previous, revised, StringComparison.Ordinal)) throw new InvalidOperationException("The reviser returned unchanged content.");
        if (!revised.TrimStart().StartsWith('#')) throw new InvalidOperationException("The revised chapter must retain a Markdown heading.");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class JsonLinesLiteraryQualityEvidenceStore : ILiteraryQualityEvidenceStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public JsonLinesLiteraryQualityEvidenceStore(string root)
    {
        _root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));
        Directory.CreateDirectory(_root);
    }

    public async ValueTask AppendAsync(LiteraryQualityAttemptEvidence evidence, CancellationToken cancellationToken)
    {
        var path = PathFor(evidence.ProjectId, evidence.ChapterNumber);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(evidence, _json) + Environment.NewLine, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<IReadOnlyList<LiteraryQualityAttemptEvidence>> LoadAsync(string projectId, int chapterNumber, CancellationToken cancellationToken)
    {
        var path = PathFor(projectId, chapterNumber);
        if (!File.Exists(path)) return [];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
            return lines.Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => JsonSerializer.Deserialize<LiteraryQualityAttemptEvidence>(x, _json) ?? throw new InvalidDataException("Invalid quality evidence line."))
                .ToArray();
        }
        finally { _gate.Release(); }
    }

    private string PathFor(string projectId, int chapterNumber)
    {
        var safe = string.Concat(projectId.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        return Path.Combine(_root, $"{safe}.chapter-{chapterNumber:D2}.quality.jsonl");
    }
}

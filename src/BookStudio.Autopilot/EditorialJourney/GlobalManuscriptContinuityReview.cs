using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BookStudio.Autopilot.EditorialJourney;

public enum GlobalContinuityDimension
{
    Chronology,
    CharacterArc,
    UnresolvedSubplot,
    Contradiction,
    Repetition,
    GlobalPacing,
    OpeningEndingCoherence,
    FactualConsistency,
}

public enum GlobalContinuityDecision { Pass, Revise, Blocked }

public sealed record GlobalContinuityIssue(
    GlobalContinuityDimension Dimension,
    string Code,
    string Description,
    IReadOnlyList<int> ChapterNumbers,
    int Severity,
    bool MaterialBlocker = false)
{
    public void Validate(int chapterCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Code);
        ArgumentException.ThrowIfNullOrWhiteSpace(Description);
        if (Severity is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(Severity));
        if (ChapterNumbers.Count == 0 || ChapterNumbers.Any(x => x < 1 || x > chapterCount))
            throw new InvalidDataException("Global continuity issue references invalid chapters.");
    }
}

public sealed record GlobalContinuityAssessment(
    GlobalContinuityDecision Decision,
    IReadOnlyList<GlobalContinuityIssue> Issues,
    string ReviewerIdentity,
    string EvidenceId);

public sealed record GlobalManuscriptRequest(
    string ProjectId,
    string Title,
    IReadOnlyDictionary<int, string> Chapters,
    string CanonicalBrief,
    string WriterIdentity)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(CanonicalBrief);
        ArgumentException.ThrowIfNullOrWhiteSpace(WriterIdentity);
        if (Chapters.Count < 2) throw new InvalidDataException("Global review requires at least two chapters.");
        var expected = Enumerable.Range(1, Chapters.Count);
        if (!Chapters.Keys.OrderBy(x => x).SequenceEqual(expected)) throw new InvalidDataException("Global review chapters must be contiguous and one-based.");
        if (Chapters.Values.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Global review contains an empty chapter.");
    }
}

public sealed record GlobalContinuityEvidence(
    string ProjectId,
    int Attempt,
    string ManuscriptSha256,
    GlobalContinuityDecision Decision,
    IReadOnlyList<GlobalContinuityIssue> Issues,
    IReadOnlyList<int> ChangedChapters,
    string ReviewerIdentity,
    DateTimeOffset OccurredAtUtc);

public sealed record GlobalManuscriptReviewResult(
    GlobalContinuityDecision Decision,
    IReadOnlyDictionary<int, string> Chapters,
    int Attempts,
    IReadOnlyList<GlobalContinuityEvidence> Evidence);

public interface IGlobalManuscriptContinuityEvaluator
{
    ValueTask<GlobalContinuityAssessment> EvaluateAsync(GlobalManuscriptRequest request, int attempt, CancellationToken cancellationToken);
}

public interface IGlobalManuscriptRepairer
{
    string Identity { get; }
    ValueTask<IReadOnlyDictionary<int, string>> RepairAsync(GlobalManuscriptRequest request, IReadOnlyList<GlobalContinuityIssue> issues, int attempt, CancellationToken cancellationToken);
}

public interface IGlobalContinuityEvidenceStore
{
    ValueTask AppendAsync(GlobalContinuityEvidence evidence, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<GlobalContinuityEvidence>> LoadAsync(string projectId, CancellationToken cancellationToken);
}

public sealed record GlobalContinuityPolicy(int MaximumRepairAttempts = 2, int BlockerSeverity = 95, int ReviseSeverity = 55)
{
    public void Validate()
    {
        if (MaximumRepairAttempts is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(MaximumRepairAttempts));
        if (BlockerSeverity is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(BlockerSeverity));
        if (ReviseSeverity is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(ReviseSeverity));
    }
}

public sealed class GlobalManuscriptContinuityReview
{
    private readonly IGlobalManuscriptContinuityEvaluator _evaluator;
    private readonly IGlobalManuscriptRepairer _repairer;
    private readonly IGlobalContinuityEvidenceStore _evidence;
    private readonly GlobalContinuityPolicy _policy;

    public GlobalManuscriptContinuityReview(IGlobalManuscriptContinuityEvaluator evaluator, IGlobalManuscriptRepairer repairer, IGlobalContinuityEvidenceStore evidence, GlobalContinuityPolicy policy)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _repairer = repairer ?? throw new ArgumentNullException(nameof(repairer));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _policy.Validate();
    }

    public async ValueTask<GlobalManuscriptReviewResult> RunAsync(GlobalManuscriptRequest original, CancellationToken cancellationToken = default)
    {
        original.Validate();
        if (string.Equals(original.WriterIdentity, _repairer.Identity, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The global repairer must be independent from the original writer.");

        var chapters = new Dictionary<int, string>(original.Chapters);
        var evidence = new List<GlobalContinuityEvidence>();
        var totalEvaluations = _policy.MaximumRepairAttempts + 1;

        for (var attempt = 1; attempt <= totalEvaluations; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = original with { Chapters = new Dictionary<int, string>(chapters) };
            var assessment = await _evaluator.EvaluateAsync(request, attempt, cancellationToken).ConfigureAwait(false);
            ValidateAssessment(request, assessment);
            if (string.Equals(assessment.ReviewerIdentity, original.WriterIdentity, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(assessment.ReviewerIdentity, _repairer.Identity, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The global reviewer must be independent from writer and repairer.");

            var deterministic = Decide(assessment.Issues);
            if (assessment.Decision != deterministic) throw new InvalidDataException("Global reviewer decision conflicts with policy.");
            var changed = Array.Empty<int>();
            var item = new GlobalContinuityEvidence(original.ProjectId, attempt, Hash(chapters), deterministic, assessment.Issues, changed, assessment.ReviewerIdentity, DateTimeOffset.UtcNow);

            if (deterministic is GlobalContinuityDecision.Pass or GlobalContinuityDecision.Blocked || attempt == totalEvaluations)
            {
                await _evidence.AppendAsync(item, cancellationToken).ConfigureAwait(false);
                evidence.Add(item);
                return new GlobalManuscriptReviewResult(deterministic, chapters, attempt, evidence);
            }

            var repaired = await _repairer.RepairAsync(request, assessment.Issues, attempt, cancellationToken).ConfigureAwait(false);
            changed = ValidateRepair(chapters, repaired, assessment.Issues);
            item = item with { ChangedChapters = changed };
            await _evidence.AppendAsync(item, cancellationToken).ConfigureAwait(false);
            evidence.Add(item);
            chapters = new Dictionary<int, string>(repaired);
        }

        throw new InvalidOperationException("Global continuity review terminated unexpectedly.");
    }

    public GlobalContinuityDecision Decide(IReadOnlyList<GlobalContinuityIssue> issues)
    {
        if (issues.Any(x => x.MaterialBlocker || x.Severity >= _policy.BlockerSeverity)) return GlobalContinuityDecision.Blocked;
        if (issues.Any(x => x.Severity >= _policy.ReviseSeverity)) return GlobalContinuityDecision.Revise;
        return GlobalContinuityDecision.Pass;
    }

    private static void ValidateAssessment(GlobalManuscriptRequest request, GlobalContinuityAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentException.ThrowIfNullOrWhiteSpace(assessment.ReviewerIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(assessment.EvidenceId);
        foreach (var issue in assessment.Issues) issue.Validate(request.Chapters.Count);
        if (assessment.Issues.Select(x => x.Code).Distinct(StringComparer.Ordinal).Count() != assessment.Issues.Count)
            throw new InvalidDataException("Global continuity issue codes must be unique per assessment.");
    }

    private static int[] ValidateRepair(IReadOnlyDictionary<int, string> previous, IReadOnlyDictionary<int, string> repaired, IReadOnlyList<GlobalContinuityIssue> issues)
    {
        if (!previous.Keys.OrderBy(x => x).SequenceEqual(repaired.Keys.OrderBy(x => x))) throw new InvalidDataException("Global repair changed the chapter set.");
        if (repaired.Values.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Global repair returned an empty chapter.");
        var changed = previous.Keys.Where(number => !string.Equals(previous[number], repaired[number], StringComparison.Ordinal)).OrderBy(x => x).ToArray();
        if (changed.Length == 0) throw new InvalidDataException("Global repair did not change the manuscript.");
        var allowed = issues.SelectMany(x => x.ChapterNumbers).Distinct().ToHashSet();
        if (changed.Any(number => !allowed.Contains(number))) throw new InvalidDataException("Global repair changed a chapter not referenced by the review.");
        return changed;
    }

    private static string Hash(IReadOnlyDictionary<int, string> chapters)
    {
        var canonical = string.Join("\n\n", chapters.OrderBy(x => x.Key).Select(x => $"# {x.Key}\n{x.Value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed class JsonLinesGlobalContinuityEvidenceStore : IGlobalContinuityEvidenceStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public JsonLinesGlobalContinuityEvidenceStore(string root)
    {
        _root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));
        Directory.CreateDirectory(_root);
    }

    public async ValueTask AppendAsync(GlobalContinuityEvidence evidence, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await File.AppendAllTextAsync(PathFor(evidence.ProjectId), JsonSerializer.Serialize(evidence, _json) + Environment.NewLine, Encoding.UTF8, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async ValueTask<IReadOnlyList<GlobalContinuityEvidence>> LoadAsync(string projectId, CancellationToken cancellationToken)
    {
        var path = PathFor(projectId);
        if (!File.Exists(path)) return [];
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        return lines.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => JsonSerializer.Deserialize<GlobalContinuityEvidence>(x, _json) ?? throw new InvalidDataException("Invalid global evidence line.")).ToArray();
    }

    private string PathFor(string projectId)
    {
        var safe = string.Concat(projectId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(_root, safe + ".global-continuity.jsonl");
    }
}

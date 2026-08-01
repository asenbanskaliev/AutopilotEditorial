using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BookStudio.Application.Authoring;

public sealed class ReaderRetentionEvaluator
{
    private const string EvaluatorIdentity = "reader-retention-deterministic-v1";

    public ReaderRetentionEvaluation Evaluate(ReaderPromise promise, IReadOnlyList<ReaderRetentionUnit> units, IReadOnlyList<ReaderCriticAssessment> critics)
    {
        ArgumentNullException.ThrowIfNull(promise);
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(critics);
        if (units.Count == 0) throw new ArgumentException("At least one unit is required.", nameof(units));

        var metrics = new List<ReaderRetentionMetric>();
        var findings = new List<ReaderRetentionFinding>();
        var risks = new List<ReaderRetentionRiskPoint>();

        foreach (var unit in units)
        {
            var unitMetrics = ScoreUnit(unit, promise);
            metrics.AddRange(unitMetrics);
            var unitFindings = BuildFindings(unit, unitMetrics);
            findings.AddRange(unitFindings);

            var criticVotes = critics.Where(c => c.UnitId == unit.UnitId).ToArray();
            findings.AddRange(criticVotes.SelectMany(c => c.Findings));
            var deterministicRisk = WeightedRisk(unitMetrics);
            var criticRisk = criticVotes.Length == 0 ? deterministicRisk : criticVotes.Average(c => c.AbandonmentProbability);
            var combined = Clamp((deterministicRisk * 0.7m) + (criticRisk * 0.3m));
            var hasBlocking = unitFindings.Any(f => f.Severity == ReaderRetentionSeverity.Blocking) || criticVotes.SelectMany(c => c.Findings).Any(f => f.Severity == ReaderRetentionSeverity.Blocking && !f.Resolved);
            var band = ToBand(combined, hasBlocking);
            risks.Add(new ReaderRetentionRiskPoint(unit.UnitId, band, combined, DominantDrivers(unitMetrics), Digest($"{unit.UnitId}|{combined}|{band}")));
        }

        var unresolved = findings.Where(f => !f.Resolved).ToArray();
        var manuscriptRisk = risks.Average(r => r.WeightedRisk);
        var manuscriptBand = ToBand(manuscriptRisk, unresolved.Any(f => f.Severity == ReaderRetentionSeverity.Blocking));
        var blocked = manuscriptBand is ReaderRetentionRiskBand.High or ReaderRetentionRiskBand.Critical || unresolved.Any(f => f.Severity is ReaderRetentionSeverity.Major or ReaderRetentionSeverity.Blocking);
        var evidence = Digest(string.Join("|", risks.OrderBy(r => r.UnitId).Select(r => r.EvidenceDigest)) + "|" + promise.PromiseDigest);

        return new ReaderRetentionEvaluation(metrics, findings, risks, manuscriptRisk, manuscriptBand, blocked, EvaluatorIdentity, evidence);
    }

    private static IReadOnlyList<ReaderRetentionMetric> ScoreUnit(ReaderRetentionUnit unit, ReaderPromise promise)
    {
        var text = unit.Text ?? string.Empty;
        var words = Regex.Matches(text, @"\p{L}+[\p{L}\p{M}'’-]*").Select(m => m.Value).ToArray();
        var sentences = Regex.Split(text, @"(?<=[.!?])\s+").Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        var paragraphs = Regex.Split(text, @"\r?\n\s*\r?\n").Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        var dialogue = Regex.Matches(text, "[\"«—]").Count;
        var questions = text.Count(c => c == '?');
        var conflictTerms = CountTerms(text, "but", "however", "against", "risk", "danger", "conflict", "pero", "sin embargo", "riesgo", "peligro", "contra");
        var desireTerms = CountTerms(text, "want", "need", "must", "hope", "desea", "quiere", "necesita", "debe", "espera");
        var changeTerms = CountTerms(text, "therefore", "suddenly", "then", "because", "por tanto", "de pronto", "entonces", "porque");
        var expositionTerms = CountTerms(text, "was", "were", "had been", "era", "había", "había sido");
        var repeated = words.GroupBy(w => w.ToLowerInvariant()).Where(g => g.Key.Length > 4).Sum(g => Math.Max(0, g.Count() - 3));
        var avgSentence = sentences.Length == 0 ? words.Length : (decimal)words.Length / sentences.Length;
        var clarity = Clamp(1m - Math.Max(0m, avgSentence - 24m) / 30m);
        var exposition = Clamp(words.Length == 0 ? 1m : (decimal)expositionTerms / Math.Max(1, words.Length / 20));
        var hook = Clamp((questions * 0.15m) + (conflictTerms * 0.08m) + (dialogue * 0.01m) + (unit.Scope == ReaderRetentionScope.Chapter ? 0.15m : 0.1m));
        var desire = Clamp(0.25m + desireTerms * 0.12m);
        var conflict = Clamp(0.2m + conflictTerms * 0.1m);
        var novelty = Clamp(0.75m - repeated * 0.02m);
        var progression = Clamp(0.25m + changeTerms * 0.08m + Math.Min(paragraphs.Length, 8) * 0.03m);
        var tension = Clamp((hook + conflict + progression) / 3m);
        var payoff = Clamp(0.25m + (text.Contains('!') ? 0.1m : 0m) + changeTerms * 0.06m);
        var emotion = Clamp(0.25m + CountTerms(text, "fear", "love", "anger", "joy", "miedo", "amor", "ira", "alegría") * 0.08m);
        var predictability = Clamp(0.25m + repeated * 0.03m);

        return new[]
        {
            Metric(unit, ReaderRetentionDimension.Hook, hook, promise.MinimumHook, 1.2m),
            Metric(unit, ReaderRetentionDimension.Desire, desire, 0.45m, 1m),
            Metric(unit, ReaderRetentionDimension.Conflict, conflict, promise.MinimumConflict, 1.2m),
            Metric(unit, ReaderRetentionDimension.Novelty, novelty, 0.55m, 0.8m),
            Metric(unit, ReaderRetentionDimension.Progression, progression, promise.MinimumProgression, 1.2m),
            Metric(unit, ReaderRetentionDimension.ExpositionLoad, exposition, promise.MaximumExpositionLoad, 1m),
            Metric(unit, ReaderRetentionDimension.Tension, tension, 0.45m, 1.1m),
            Metric(unit, ReaderRetentionDimension.Payoff, payoff, promise.MinimumPayoff, 1m),
            Metric(unit, ReaderRetentionDimension.EmotionalConnection, emotion, 0.4m, 1m),
            Metric(unit, ReaderRetentionDimension.Clarity, clarity, 0.65m, 1m),
            Metric(unit, ReaderRetentionDimension.Predictability, predictability, 0.55m, 0.8m)
        };
    }

    private static ReaderRetentionMetric Metric(ReaderRetentionUnit unit, ReaderRetentionDimension dimension, decimal score, decimal threshold, decimal weight)
        => new(dimension, score, threshold, weight, $"unit={unit.UnitId};dimension={dimension};score={score:0.000}", Digest($"{unit.ContentDigest}|{dimension}|{score:0.000}|{threshold:0.000}"));

    private static IReadOnlyList<ReaderRetentionFinding> BuildFindings(ReaderRetentionUnit unit, IReadOnlyList<ReaderRetentionMetric> metrics)
    {
        var list = new List<ReaderRetentionFinding>();
        foreach (var metric in metrics)
        {
            var inverse = metric.Dimension is ReaderRetentionDimension.ExpositionLoad or ReaderRetentionDimension.Predictability;
            var failed = inverse ? metric.Score > metric.Threshold : metric.Score < metric.Threshold;
            if (!failed) continue;
            var delta = Math.Abs(metric.Score - metric.Threshold);
            var severity = delta >= 0.35m ? ReaderRetentionSeverity.Blocking : delta >= 0.2m ? ReaderRetentionSeverity.Major : ReaderRetentionSeverity.Minor;
            var id = Digest($"{unit.UnitId}|{metric.Dimension}|{metric.EvidenceDigest}")[..16];
            list.Add(new ReaderRetentionFinding(id, $"RET-{metric.Dimension.ToString().ToUpperInvariant()}", severity, unit.Scope, unit.UnitId,
                $"{metric.Dimension} is outside the reader-promise threshold.", metric.Score, metric.Threshold, 1m, metric.EvidenceDigest, false));
        }
        return list;
    }

    private static decimal WeightedRisk(IReadOnlyList<ReaderRetentionMetric> metrics)
    {
        decimal weighted = 0m, total = 0m;
        foreach (var metric in metrics)
        {
            var inverse = metric.Dimension is ReaderRetentionDimension.ExpositionLoad or ReaderRetentionDimension.Predictability;
            var risk = inverse ? metric.Score : 1m - metric.Score;
            weighted += Clamp(risk) * metric.Weight;
            total += metric.Weight;
        }
        return total == 0m ? 1m : Clamp(weighted / total);
    }

    private static IReadOnlyList<string> DominantDrivers(IReadOnlyList<ReaderRetentionMetric> metrics) => metrics
        .Select(m => new { m.Dimension, Risk = m.Dimension is ReaderRetentionDimension.ExpositionLoad or ReaderRetentionDimension.Predictability ? m.Score : 1m - m.Score })
        .OrderByDescending(x => x.Risk).ThenBy(x => x.Dimension).Take(3).Select(x => x.Dimension.ToString()).ToArray();

    private static ReaderRetentionRiskBand ToBand(decimal risk, bool blocking) => blocking || risk >= 0.75m
        ? ReaderRetentionRiskBand.Critical
        : risk >= 0.55m ? ReaderRetentionRiskBand.High
        : risk >= 0.30m ? ReaderRetentionRiskBand.Medium
        : ReaderRetentionRiskBand.Low;

    private static int CountTerms(string text, params string[] terms) => terms.Sum(term => Regex.Matches(text, $@"\b{Regex.Escape(term)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count);
    private static decimal Clamp(decimal value) => Math.Min(1m, Math.Max(0m, value));
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

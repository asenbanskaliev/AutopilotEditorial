using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BookStudio.Application.Artifacts;

namespace BookStudio.Application.Quality;

/// <summary>Reads verified immutable drafts and produces deterministic quality checks.</summary>
public sealed partial class QualityAssessmentService : IQualityAssessmentService
{
    public const int MaximumAuditBytes = 2 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly IArtifactStore _store;

    public QualityAssessmentService(IArtifactStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<QualityAuditResult> RunAuditAsync(
        QualityAuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateScope(query.ProjectId, query.ArtifactId);
        if (query.Version < 1)
        {
            throw new QualityAssessmentException("invalid_version", "Artifact version must be positive.");
        }
        if (query.MinimumWords is < 1 or > 50_000)
        {
            throw new QualityAssessmentException("invalid_minimum_words", "minimumWords must be between 1 and 50000.");
        }
        if (query.MaximumSentenceWords is < 10 or > 300)
        {
            throw new QualityAssessmentException("invalid_sentence_limit", "maximumSentenceWords must be between 10 and 300.");
        }

        var (manifest, text) = await ReadTextAsync(
                query.ArtifactId,
                query.Version,
                cancellationToken)
            .ConfigureAwait(false);
        var lines = SplitLines(text);
        var paragraphs = SplitParagraphs(lines);
        var sentences = SplitSentences(text);
        var placeholderCount = PlaceholderRegex().Matches(text).Count;
        var duplicateCount = CountAdjacentDuplicates(paragraphs);
        var longSentenceCount = sentences.Count(sentence => CountWords(sentence) > query.MaximumSentenceWords);
        var metrics = new QualityMetrics(
            text.Length,
            CountWords(text),
            lines.Length,
            paragraphs.Count,
            lines.Count(line => line.TrimStart().StartsWith('#')),
            sentences.Count,
            placeholderCount,
            duplicateCount,
            longSentenceCount);
        var checks = BuildChecks(metrics, query.MinimumWords, query.MaximumSentenceWords);
        return new QualityAuditResult(
            ToReference(query.ProjectId, manifest),
            metrics,
            checks,
            checks.All(check => !string.Equals(check.Status, "fail", StringComparison.Ordinal)));
    }

    public async ValueTask<QualityGateResult> EvaluateGateAsync(
        QualityGateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!string.Equals(query.Profile, "draft-basic", StringComparison.Ordinal))
        {
            throw new QualityAssessmentException("unknown_quality_profile", "Only the draft-basic quality profile is available.");
        }
        if (query.MaximumWarnings is < 0 or > 100)
        {
            throw new QualityAssessmentException("invalid_warning_limit", "maximumWarnings must be between 0 and 100.");
        }

        var audit = await RunAuditAsync(
                new QualityAuditQuery(
                    query.ProjectId,
                    query.ArtifactId,
                    query.Version,
                    query.MinimumWords,
                    MaximumSentenceWords: 60),
                cancellationToken)
            .ConfigureAwait(false);
        var reasons = audit.Checks
            .Where(check => string.Equals(check.Status, "fail", StringComparison.Ordinal))
            .Select(check => check.Id)
            .ToList();
        var warningCount = audit.Checks.Count(check => string.Equals(check.Status, "warn", StringComparison.Ordinal));
        if (warningCount > query.MaximumWarnings)
        {
            reasons.Add("quality.maximum_warnings");
        }
        if (!query.BlockOnPlaceholders)
        {
            reasons.RemoveAll(reason => string.Equals(reason, "content.no_placeholders", StringComparison.Ordinal));
        }

        return new QualityGateResult(
            query.Profile,
            reasons.Count == 0 ? "PASS" : "BLOCKED",
            audit,
            reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    public static string BuildOperationId(params string[] parts)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join('|', parts)));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    private async ValueTask<(ArtifactManifest Manifest, string Text)> ReadTextAsync(
        string artifactId,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            var manifest = await _store.GetManifestAsync(artifactId, version, cancellationToken)
                .ConfigureAwait(false);
            if (!IsSupportedText(manifest.MediaType))
            {
                throw new QualityAssessmentException("unsupported_media_type", "Quality assessment requires a text draft.");
            }
            if (manifest.Length > MaximumAuditBytes)
            {
                throw new QualityAssessmentException("artifact_too_large", "Artifact exceeds the bounded quality assessment limit.");
            }

            await using var stream = await _store.OpenReadAsync(
                    artifactId,
                    version,
                    verifyIntegrity: true,
                    cancellationToken)
                .ConfigureAwait(false);
            using var memory = new MemoryStream((int)manifest.Length);
            await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            if (memory.Length != manifest.Length || memory.Length > MaximumAuditBytes)
            {
                throw new QualityAssessmentException("artifact_integrity_failed", "Artifact length verification failed.");
            }

            try
            {
                return (
                    manifest,
                    StrictUtf8.GetString(memory.GetBuffer(), 0, checked((int)memory.Length)));
            }
            catch (DecoderFallbackException)
            {
                throw new QualityAssessmentException("invalid_utf8", "Artifact is not valid UTF-8 text.");
            }
        }
        catch (QualityAssessmentException)
        {
            throw;
        }
        catch (ArtifactNotFoundException)
        {
            throw new QualityAssessmentException("artifact_not_found", "The requested artifact version was not found.");
        }
        catch (ArtifactIntegrityException)
        {
            throw new QualityAssessmentException("artifact_integrity_failed", "Artifact integrity verification failed.");
        }
        catch (ArgumentException)
        {
            throw new QualityAssessmentException("invalid_artifact", "Artifact reference is invalid.");
        }
    }

    private static IReadOnlyList<QualityCheck> BuildChecks(
        QualityMetrics metrics,
        int minimumWords,
        int maximumSentenceWords)
    {
        return
        [
            Check(
                "content.non_empty",
                metrics.Characters > 0 && metrics.Words > 0 ? "pass" : "fail",
                metrics.Words,
                1,
                "Draft must contain non-whitespace text."),
            Check(
                "content.minimum_words",
                metrics.Words >= minimumWords ? "pass" : "fail",
                metrics.Words,
                minimumWords,
                "Draft must meet the configured minimum word count."),
            Check(
                "content.no_placeholders",
                metrics.PlaceholderCount == 0 ? "pass" : "fail",
                metrics.PlaceholderCount,
                0,
                "Draft must not contain TODO, TBD, FIXME or XXX placeholders."),
            Check(
                "content.no_adjacent_duplicate_paragraphs",
                metrics.AdjacentDuplicateParagraphs == 0 ? "pass" : "warn",
                metrics.AdjacentDuplicateParagraphs,
                0,
                "Adjacent duplicate paragraphs should be removed."),
            Check(
                "style.maximum_sentence_words",
                metrics.LongSentenceCount == 0 ? "pass" : "warn",
                metrics.LongSentenceCount,
                maximumSentenceWords,
                "Sentences above the configured word limit should be reviewed."),
            Check(
                "structure.has_paragraphs",
                metrics.Paragraphs > 0 ? "pass" : "fail",
                metrics.Paragraphs,
                1,
                "Draft must contain at least one paragraph."),
        ];
    }

    private static QualityCheck Check(
        string id,
        string status,
        int observed,
        int threshold,
        string message) =>
        new(id, status, observed, threshold, message);

    private static int CountWords(string text) =>
        WordRegex().Matches(text).Count;

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static IReadOnlyList<string> SplitParagraphs(IReadOnlyList<string> lines)
    {
        var paragraphs = new List<string>();
        var current = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph(current, paragraphs);
            }
            else
            {
                current.Add(line.Trim());
            }
        }
        FlushParagraph(current, paragraphs);
        return paragraphs;
    }

    private static void FlushParagraph(
        ICollection<string> current,
        ICollection<string> paragraphs)
    {
        if (current.Count == 0)
        {
            return;
        }
        paragraphs.Add(NormalizeWhitespace(string.Join(' ', current)));
        current.Clear();
    }

    private static IReadOnlyList<string> SplitSentences(string text) =>
        SentenceBoundaryRegex().Split(text)
            .Select(NormalizeWhitespace)
            .Where(sentence => sentence.Length > 0)
            .ToArray();

    private static int CountAdjacentDuplicates(IReadOnlyList<string> paragraphs)
    {
        var count = 0;
        for (var index = 1; index < paragraphs.Count; index++)
        {
            if (string.Equals(paragraphs[index - 1], paragraphs[index], StringComparison.Ordinal))
            {
                count++;
            }
        }
        return count;
    }

    private static string NormalizeWhitespace(string value) =>
        WhitespaceRegex().Replace(value.Trim(), " ");

    private static bool IsSupportedText(string mediaType) =>
        string.Equals(mediaType, "text/markdown", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mediaType, "text/plain", StringComparison.OrdinalIgnoreCase);

    private static void ValidateScope(string projectId, string artifactId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || !ProjectIdRegex().IsMatch(projectId))
        {
            throw new QualityAssessmentException("invalid_project_id", "Project ID is invalid.");
        }
        if (string.IsNullOrWhiteSpace(artifactId) || !ArtifactIdRegex().IsMatch(artifactId))
        {
            throw new QualityAssessmentException("invalid_artifact_id", "Artifact ID is invalid.");
        }
        if (!artifactId.StartsWith(projectId + ".draft.", StringComparison.Ordinal))
        {
            throw new QualityAssessmentException("quality_scope_violation", "Artifact does not belong to the requested project draft scope.");
        }
    }

    private static QualityArtifactReference ToReference(
        string projectId,
        ArtifactManifest manifest) =>
        new(
            manifest.ArtifactId,
            manifest.Version,
            manifest.Sha256,
            manifest.Length,
            manifest.MediaType,
            $"book://project/{projectId}/artifact/{manifest.ArtifactId}/versions/{manifest.Version.ToString(CultureInfo.InvariantCulture)}");

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectIdRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactIdRegex();

    [GeneratedRegex(@"\b(?:TODO|TBD|FIXME|XXX)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"(?<=[.!?])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBoundaryRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}

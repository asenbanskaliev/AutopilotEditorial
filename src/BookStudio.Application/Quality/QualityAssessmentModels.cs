namespace BookStudio.Application.Quality;

public sealed record QualityAuditQuery(
    string ProjectId,
    string ArtifactId,
    int Version,
    int MinimumWords = 1,
    int MaximumSentenceWords = 60);

public sealed record QualityGateQuery(
    string ProjectId,
    string ArtifactId,
    int Version,
    string Profile = "draft-basic",
    int MinimumWords = 1,
    int MaximumWarnings = 3,
    bool BlockOnPlaceholders = true);

public sealed record QualityArtifactReference(
    string ArtifactId,
    int Version,
    string Sha256,
    long Length,
    string MediaType,
    string Uri);

public sealed record QualityMetrics(
    int Characters,
    int Words,
    int Lines,
    int Paragraphs,
    int MarkdownHeadings,
    int Sentences,
    int PlaceholderCount,
    int AdjacentDuplicateParagraphs,
    int LongSentenceCount);

public sealed record QualityCheck(
    string Id,
    string Status,
    int Observed,
    int Threshold,
    string Message);

public sealed record QualityAuditResult(
    QualityArtifactReference Artifact,
    QualityMetrics Metrics,
    IReadOnlyList<QualityCheck> Checks,
    bool IsPassing);

public sealed record QualityGateResult(
    string Profile,
    string Decision,
    QualityAuditResult Audit,
    IReadOnlyList<string> BlockingReasons);

public sealed class QualityAssessmentException : Exception
{
    public QualityAssessmentException(string code, string safeMessage)
        : base(safeMessage)
    {
        Code = code;
    }

    public string Code { get; }
}

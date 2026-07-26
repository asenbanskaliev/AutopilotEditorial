namespace BookStudio.Application.Authoring;

public sealed record DraftRegistrationCommand(
    string ProjectId,
    string ArtifactId,
    int ExpectedVersion,
    string MediaType,
    string Content);

public sealed record DraftValidationQuery(
    string ProjectId,
    string ArtifactId,
    int Version,
    int MaximumLineLength = 120);

public sealed record DraftResourceQuery(
    string ProjectId,
    string ArtifactId,
    int Version);

public sealed record DraftArtifactReference(
    string ArtifactId,
    int Version,
    string Sha256,
    long Length,
    string MediaType,
    string Uri);

public sealed record DraftRegistrationResult(
    DraftArtifactReference Artifact,
    IReadOnlyList<DraftWarning> Warnings);

public sealed record DraftValidationMetrics(
    int Characters,
    int Words,
    int Lines,
    int Paragraphs,
    int MarkdownHeadings);

public sealed record DraftWarning(
    string Code,
    string Message,
    int Count);

public sealed record DraftValidationResult(
    DraftArtifactReference Artifact,
    DraftValidationMetrics Metrics,
    IReadOnlyList<DraftWarning> Warnings,
    bool IsValid);

public sealed record DraftResourceResult(
    DraftArtifactReference Artifact,
    string Text);

public sealed class DraftAuthoringException : Exception
{
    public DraftAuthoringException(string code, string safeMessage)
        : base(safeMessage)
    {
        Code = code;
    }

    public string Code { get; }
}

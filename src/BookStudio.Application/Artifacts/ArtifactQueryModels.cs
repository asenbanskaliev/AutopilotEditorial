namespace BookStudio.Application.Artifacts;

public sealed record ArtifactGetQuery(
    string ProjectId,
    string ArtifactId,
    int Version,
    bool IncludeContent);

public sealed record ArtifactCompareQuery(
    string ProjectId,
    string ArtifactId,
    int LeftVersion,
    int RightVersion,
    int MaxDifferences);

public sealed record ArtifactResourceQuery(
    string ProjectId,
    string ArtifactId,
    int Version);

public sealed record ArtifactLogicalReference(
    string ArtifactId,
    int Version,
    string Sha256,
    long Length,
    string MediaType,
    DateTimeOffset CreatedAtUtc,
    string Uri);

public sealed record ArtifactGetResult(
    ArtifactLogicalReference Artifact,
    string? InlineText,
    bool ContentIncluded,
    IReadOnlyList<string> Warnings);

public sealed record ArtifactLineDifference(
    string Kind,
    int? LeftLine,
    int? RightLine,
    string? LeftText,
    string? RightText);

public sealed record ArtifactComparisonSummary(
    int AddedLines,
    int RemovedLines,
    int DifferenceCount,
    bool DifferencesTruncated,
    bool TextDiffPerformed);

public sealed record ArtifactCompareResult(
    ArtifactLogicalReference Left,
    ArtifactLogicalReference Right,
    bool Identical,
    ArtifactComparisonSummary Summary,
    IReadOnlyList<ArtifactLineDifference> Differences,
    IReadOnlyList<string> Warnings);

public sealed record ArtifactResourceResult(
    ArtifactLogicalReference Artifact,
    string? Text,
    string? BlobBase64);

public sealed class ArtifactQueryException : Exception
{
    public ArtifactQueryException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

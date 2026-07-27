namespace BookStudio.Application.Production;

public sealed record ReleaseSourceRequest(
    string Role,
    string ArtifactId,
    int Version);

public sealed record ReleasePreparationCommand(
    string ProjectId,
    string ReleaseId,
    int ExpectedVersion,
    string Title,
    string Language,
    IReadOnlyList<ReleaseSourceRequest> Sources);

public sealed record ReleasePreflightQuery(
    string ProjectId,
    string ReleaseArtifactId,
    int Version,
    string Profile = "release-basic");

public sealed record ReleaseArtifactReference(
    string ArtifactId,
    int Version,
    string Sha256,
    long Length,
    string MediaType,
    string Uri);

public sealed record ReleaseManifestSource(
    string Role,
    string ArtifactId,
    int Version,
    string Sha256,
    long Length,
    string MediaType);

public sealed record ReleaseManifestDocument(
    string SchemaVersion,
    string ProjectId,
    string ReleaseId,
    string Title,
    string Language,
    IReadOnlyList<ReleaseManifestSource> Sources);

public sealed record ReleasePreparationResult(
    ReleaseArtifactReference Release,
    ReleaseManifestDocument Manifest);

public sealed record ReleasePreflightCheck(
    string Id,
    string Status,
    int Observed,
    int Threshold,
    string Message);

public sealed record ReleasePreflightResult(
    string Profile,
    string Decision,
    ReleaseArtifactReference Release,
    ReleaseManifestDocument Manifest,
    IReadOnlyList<ReleasePreflightCheck> Checks,
    IReadOnlyList<string> BlockingReasons);

public sealed class ReleaseProductionException : Exception
{
    public ReleaseProductionException(string code, string safeMessage)
        : base(safeMessage)
    {
        Code = code;
    }

    public string Code { get; }
}

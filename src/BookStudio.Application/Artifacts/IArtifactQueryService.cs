namespace BookStudio.Application.Artifacts;

/// <summary>Provider-neutral read and comparison use cases for immutable artifacts.</summary>
public interface IArtifactQueryService
{
    ValueTask<ArtifactGetResult> GetAsync(
        ArtifactGetQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<ArtifactCompareResult> CompareAsync(
        ArtifactCompareQuery query,
        CancellationToken cancellationToken = default);

    ValueTask<ArtifactResourceResult> ReadResourceAsync(
        ArtifactResourceQuery query,
        CancellationToken cancellationToken = default);
}

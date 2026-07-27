using BookStudio.Application.Artifacts;

namespace BookStudio.Application.Production;

/// <summary>Maps provider quota failures to the stable production error surface.</summary>
public sealed class QuotaSafeReleaseProductionService : IReleaseProductionService
{
    private readonly IReleaseProductionService _inner;

    public QuotaSafeReleaseProductionService(IReleaseProductionService inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async ValueTask<ReleasePreparationResult> PrepareAsync(
        ReleasePreparationCommand command,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _inner.PrepareAsync(command, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArtifactStoreQuotaExceededException)
        {
            throw new ReleaseProductionException(
                "artifact_store_quota_exceeded",
                "Artifact Store quota prevents publishing this release version.");
        }
    }

    public ValueTask<ReleasePreflightResult> RunPreflightAsync(
        ReleasePreflightQuery query,
        CancellationToken cancellationToken = default) =>
        _inner.RunPreflightAsync(query, cancellationToken);
}

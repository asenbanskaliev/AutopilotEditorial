namespace BookStudio.Application.Production;

/// <summary>Provider-neutral immutable release preparation and deterministic preflight.</summary>
public interface IReleaseProductionService
{
    ValueTask<ReleasePreparationResult> PrepareAsync(
        ReleasePreparationCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<ReleasePreflightResult> RunPreflightAsync(
        ReleasePreflightQuery query,
        CancellationToken cancellationToken = default);
}

namespace BookStudio.Application.Diagnostics;

/// <summary>Provider-neutral readiness check used by local and remote hosts.</summary>
public interface IReadinessProbe
{
    string Name { get; }

    ValueTask<ReadinessProbeResult> CheckAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Sanitized readiness result. It must never contain paths, secrets or exception messages.</summary>
public sealed record ReadinessProbeResult(
    string Name,
    bool Ready,
    string Status,
    int? AppliedMigrationCount = null,
    int? LatestMigrationVersion = null);

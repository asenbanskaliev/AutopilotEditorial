using BookStudio.Application.Diagnostics;
using BookStudio.Application.Persistence;

namespace BookStudio.Infrastructure.Diagnostics;

/// <summary>Reports sanitized workspace-database readiness.</summary>
public sealed class WorkspaceDatabaseReadinessProbe : IReadinessProbe
{
    private readonly IWorkspaceDatabaseLifecycle _database;

    public WorkspaceDatabaseReadinessProbe(IWorkspaceDatabaseLifecycle database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public string Name => "workspace-database";

    public async ValueTask<ReadinessProbeResult> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var health = await _database.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            return new ReadinessProbeResult(
                Name,
                health.IsHealthy,
                health.IsHealthy ? "ready" : health.Exists ? "unhealthy" : "missing",
                health.AppliedMigrationCount,
                health.LatestMigrationVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ReadinessProbeResult(Name, false, "error");
        }
    }
}

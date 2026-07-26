using BookStudio.Application.Persistence;

namespace BookStudio.ControlCenter;

/// <summary>Initializes durable storage without turning dependency failure into process death.</summary>
public sealed class WorkspaceDatabaseInitializationService : IHostedService
{
    private readonly IWorkspaceDatabaseLifecycle _database;
    private readonly ILogger<WorkspaceDatabaseInitializationService> _logger;

    public WorkspaceDatabaseInitializationService(
        IWorkspaceDatabaseLifecycle database,
        ILogger<WorkspaceDatabaseInitializationService> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Workspace database initialization failed with type {FailureType}; readiness remains false.",
                exception.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

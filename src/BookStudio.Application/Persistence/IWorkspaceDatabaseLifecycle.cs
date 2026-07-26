namespace BookStudio.Application.Persistence;

/// <summary>
/// Manages the durable workspace database without exposing provider-specific types.
/// </summary>
public interface IWorkspaceDatabaseLifecycle
{
    ValueTask<WorkspaceDatabaseHealth> InitializeAsync(
        CancellationToken cancellationToken = default);

    ValueTask<WorkspaceDatabaseHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default);

    ValueTask BackupAsync(
        string destinationPath,
        CancellationToken cancellationToken = default);
}

namespace BookStudio.Application.Operations;

/// <summary>Provider-neutral read-only operations status and diagnostics.</summary>
public interface IOperationsDiagnosticsService
{
    ValueTask<OperationsStatusResult> GetStatusAsync(
        CancellationToken cancellationToken = default);

    ValueTask<OperationsDiagnosticsResult> RunDiagnosticsAsync(
        CancellationToken cancellationToken = default);
}

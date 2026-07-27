namespace BookStudio.Application.OpenCode;

/// <summary>Provider-neutral OpenCode project/global event and status reconciliation stream.</summary>
public interface IOpenCodeEventReconciler
{
    IAsyncEnumerable<OpenCodeReconciledEvent> WatchAsync(
        OpenCodeEventWatchRequest request,
        CancellationToken cancellationToken = default);
}

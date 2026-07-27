using BookStudio.Application.Production;
using BookStudio.Infrastructure.Artifacts.FileSystem;

namespace BookStudio.Mcp.Production;

/// <summary>Lazily composes release production use cases for one MCP process.</summary>
public sealed class BookProductionRuntime : IAsyncDisposable
{
    private readonly string _workspaceRoot;
    private readonly object _gate = new();
    private FileArtifactStore? _store;
    private ReleaseProductionService? _service;
    private int _disposed;

    public BookProductionRuntime(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public IReleaseProductionService GetService()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_service is not null)
            {
                return _service;
            }

            _store = new FileArtifactStore(FileArtifactStoreOptions.Create(_workspaceRoot));
            _service = new ReleaseProductionService(_store);
            return _service;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        FileArtifactStore? store;
        lock (_gate)
        {
            store = _store;
            _store = null;
            _service = null;
        }

        if (store is not null)
        {
            await store.DisposeAsync().ConfigureAwait(false);
        }
    }
}

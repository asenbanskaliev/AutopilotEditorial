using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Artifacts.FileSystem;

namespace BookStudio.Mcp.Authoring;

/// <summary>Lazily composes the draft authoring use case for one MCP authoring process.</summary>
public sealed class BookAuthoringRuntime : IAsyncDisposable
{
    private readonly string _workspaceRoot;
    private readonly object _gate = new();
    private FileArtifactStore? _store;
    private DraftAuthoringService? _service;
    private int _disposed;

    public BookAuthoringRuntime(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public IDraftAuthoringService GetService()
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
            _service = new DraftAuthoringService(_store);
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

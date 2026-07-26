using BookStudio.Application.Quality;
using BookStudio.Infrastructure.Artifacts.FileSystem;

namespace BookStudio.Mcp.Quality;

/// <summary>Lazily composes the deterministic quality use case for one MCP process.</summary>
public sealed class BookQualityRuntime : IAsyncDisposable
{
    private readonly string _workspaceRoot;
    private readonly object _gate = new();
    private FileArtifactStore? _store;
    private QualityAssessmentService? _service;
    private int _disposed;

    public BookQualityRuntime(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public IQualityAssessmentService GetService()
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
            _service = new QualityAssessmentService(_store);
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

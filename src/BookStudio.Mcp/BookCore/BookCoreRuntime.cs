using BookStudio.Application.Artifacts;
using BookStudio.Infrastructure.Artifacts.FileSystem;

namespace BookStudio.Mcp.BookCore;

/// <summary>Lazily composes the real artifact query use case for one MCP process.</summary>
public sealed class BookCoreRuntime : IAsyncDisposable
{
    private readonly McpHostOptions _options;
    private readonly object _gate = new();
    private FileArtifactStore? _store;
    private ArtifactQueryService? _service;
    private int _disposed;

    public BookCoreRuntime(McpHostOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public IArtifactQueryService GetQueryService()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (_service is not null)
            {
                return _service;
            }

            _store = new FileArtifactStore(
                FileArtifactStoreOptions.Create(
                    _options.WorkspaceRoot,
                    _options.MaximumArtifactBytes,
                    _options.MaximumStoreBytes,
                    _options.MaximumStoreFiles));
            _service = new ArtifactQueryService(_store);
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
            _service = null;
            _store = null;
        }

        if (store is not null)
        {
            await store.DisposeAsync().ConfigureAwait(false);
        }
    }
}

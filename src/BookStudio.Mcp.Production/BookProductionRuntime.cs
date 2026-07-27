using BookStudio.Application.Production;
using BookStudio.Infrastructure.Artifacts.FileSystem;

namespace BookStudio.Mcp.Production;

/// <summary>Lazily composes release production use cases for one MCP process.</summary>
public sealed class BookProductionRuntime : IAsyncDisposable
{
    private readonly McpHostOptions _options;
    private readonly object _gate = new();
    private FileArtifactStore? _store;
    private IReleaseProductionService? _service;
    private int _disposed;

    public BookProductionRuntime(McpHostOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
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

            _store = new FileArtifactStore(
                FileArtifactStoreOptions.Create(
                    _options.WorkspaceRoot,
                    _options.MaximumArtifactBytes,
                    _options.MaximumStoreBytes,
                    _options.MaximumStoreFiles));
            _service = new QuotaSafeReleaseProductionService(
                new ReleaseProductionService(_store));
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

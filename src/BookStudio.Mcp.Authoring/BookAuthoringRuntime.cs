using BookStudio.Application.Authoring;
using BookStudio.Infrastructure.Artifacts.FileSystem;

namespace BookStudio.Mcp.Authoring;

/// <summary>Lazily composes the draft authoring use case for one MCP authoring process.</summary>
public sealed class BookAuthoringRuntime : IAsyncDisposable
{
    private readonly McpHostOptions _options;
    private readonly object _gate = new();
    private FileArtifactStore? _store;
    private DraftAuthoringService? _service;
    private int _disposed;

    public BookAuthoringRuntime(McpHostOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
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

            _store = new FileArtifactStore(
                FileArtifactStoreOptions.Create(
                    _options.WorkspaceRoot,
                    _options.MaximumArtifactBytes,
                    _options.MaximumStoreBytes,
                    _options.MaximumStoreFiles));
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

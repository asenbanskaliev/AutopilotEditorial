using BookStudio.Application.Diagnostics;
using BookStudio.Application.Operations;
using BookStudio.Infrastructure.Diagnostics;
using BookStudio.Infrastructure.Persistence.Sqlite;

namespace BookStudio.Mcp.Ops;

/// <summary>Lazily composes read-only operations diagnostics for one MCP process.</summary>
public sealed class BookOpsRuntime : IAsyncDisposable
{
    private readonly string _workspaceRoot;
    private readonly object _gate = new();
    private SqliteWorkspaceDatabase? _database;
    private OperationsDiagnosticsService? _service;
    private int _disposed;

    public BookOpsRuntime(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public IOperationsDiagnosticsService GetService()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_service is not null)
            {
                return _service;
            }

            _database = new SqliteWorkspaceDatabase(
                SqliteWorkspaceOptions.Create(_workspaceRoot));
            IReadinessProbe probe = new WorkspaceDatabaseReadinessProbe(_database);
            _service = new OperationsDiagnosticsService(
                [probe],
                OperationsCapabilityCatalog.All);
            return _service;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        SqliteWorkspaceDatabase? database;
        lock (_gate)
        {
            database = _database;
            _database = null;
            _service = null;
        }

        if (database is not null)
        {
            await database.DisposeAsync().ConfigureAwait(false);
        }
    }
}

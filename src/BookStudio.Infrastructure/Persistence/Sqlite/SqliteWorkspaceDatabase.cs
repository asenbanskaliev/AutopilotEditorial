using BookStudio.Application.Persistence;
using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite;

/// <summary>
/// Provides the durable SQLite lifecycle and serialized metadata writes for one workspace.
/// </summary>
public sealed class SqliteWorkspaceDatabase : IWorkspaceDatabaseLifecycle, IAsyncDisposable
{
    private readonly SqliteWorkspaceOptions _options;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteMigrationRunner _migrationRunner;
    private readonly SqliteWriteQueue _writeQueue;

    public SqliteWorkspaceDatabase(SqliteWorkspaceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connectionFactory = new SqliteConnectionFactory(options);
        _migrationRunner = new SqliteMigrationRunner(new SqliteMigrationCatalog());
        _writeQueue = new SqliteWriteQueue(_connectionFactory, options.WriteQueueCapacity);
    }

    public string DatabasePath => _options.DatabasePath;

    public async ValueTask<WorkspaceDatabaseHealth> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.WorkspaceRoot);

        await _writeQueue.ExecuteExclusiveAsync(
            (connection, token) =>
            {
                token.ThrowIfCancellationRequested();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA journal_mode = WAL;";
                    var journalMode = Convert.ToString(
                        command.ExecuteScalar(),
                        System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"SQLite did not enter WAL mode. Actual mode: {journalMode ?? "<null>"}.");
                    }
                }

                _migrationRunner.ApplyPending(connection);
                return true;
            },
            cancellationToken).ConfigureAwait(false);

        return await CheckHealthAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<WorkspaceDatabaseHealth> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(_options.DatabasePath))
        {
            return ValueTask.FromResult(new WorkspaceDatabaseHealth(
                Exists: false,
                JournalMode: string.Empty,
                ForeignKeysEnabled: false,
                AppliedMigrationCount: 0,
                LatestMigrationVersion: null,
                IntegrityResult: "missing",
                FileLengthBytes: 0));
        }

        using var connection = _connectionFactory.OpenConnection();
        var health = ReadHealth(connection);
        return ValueTask.FromResult(health);
    }

    public async ValueTask BackupAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var canonicalDestination = Path.GetFullPath(destinationPath);
        if (string.Equals(
                canonicalDestination,
                _options.DatabasePath,
                SqliteWorkspaceOptions.PathComparison))
        {
            throw new ArgumentException(
                "The backup destination must differ from the source database.",
                nameof(destinationPath));
        }

        var parent = Path.GetDirectoryName(canonicalDestination)
            ?? throw new InvalidOperationException("The backup destination has no parent directory.");
        Directory.CreateDirectory(parent);

        await _writeQueue.ExecuteExclusiveAsync(
            (source, token) =>
            {
                token.ThrowIfCancellationRequested();
                Checkpoint(source, "FULL");
                DeleteDatabaseFiles(canonicalDestination);

                try
                {
                    var builder = new SqliteConnectionStringBuilder
                    {
                        DataSource = canonicalDestination,
                        Mode = SqliteOpenMode.ReadWriteCreate,
                        Cache = SqliteCacheMode.Private,
                        ForeignKeys = true,
                        Pooling = false,
                        DefaultTimeout = _options.BusyTimeoutSeconds,
                    };
                    using var destination = new SqliteConnection(builder.ToString());
                    destination.Open();
                    source.BackupDatabase(destination);

                    var integrity = ReadScalarString(destination, "PRAGMA quick_check;");
                    if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"SQLite backup integrity check failed: {integrity}.");
                    }
                }
                catch
                {
                    DeleteDatabaseFiles(canonicalDestination);
                    throw;
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask SetMetadataAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        return _writeQueue.ExecuteInTransactionAsync(
            (connection, transaction, token) =>
            {
                token.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO workspace_metadata(key, value, updated_at_utc)
                    VALUES ($key, $value, $updatedAtUtc)
                    ON CONFLICT(key) DO UPDATE SET
                        value = excluded.value,
                        updated_at_utc = excluded.updated_at_utc;
                    """;
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$value", value);
                command.Parameters.AddWithValue(
                    "$updatedAtUtc",
                    DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
                return true;
            },
            cancellationToken).AsVoid();
    }

    public ValueTask<string?> GetMetadataAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _connectionFactory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM workspace_metadata WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return ValueTask.FromResult(command.ExecuteScalar() as string);
    }

    public ValueTask<int> CountMetadataAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = _connectionFactory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM workspace_metadata;";
        return ValueTask.FromResult(Convert.ToInt32(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture));
    }

    public ValueTask DisposeAsync() => _writeQueue.DisposeAsync();

    private WorkspaceDatabaseHealth ReadHealth(SqliteConnection connection)
    {
        var journalMode = ReadScalarString(connection, "PRAGMA journal_mode;");
        var foreignKeys = ReadScalarInt32(connection, "PRAGMA foreign_keys;") == 1;
        var integrity = ReadScalarString(connection, "PRAGMA quick_check;");

        var migrationCount = 0;
        int? latestMigration = null;
        if (TableExists(connection, "schema_migrations"))
        {
            migrationCount = ReadScalarInt32(
                connection,
                "SELECT COUNT(*) FROM schema_migrations;");
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
            var value = command.ExecuteScalar();
            if (value is not null and not DBNull)
            {
                latestMigration = Convert.ToInt32(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        var fileLength = File.Exists(_options.DatabasePath)
            ? new FileInfo(_options.DatabasePath).Length
            : 0;

        return new WorkspaceDatabaseHealth(
            Exists: true,
            JournalMode: journalMode,
            ForeignKeysEnabled: foreignKeys,
            AppliedMigrationCount: migrationCount,
            LatestMigrationVersion: latestMigration,
            IntegrityResult: integrity,
            FileLengthBytes: fileLength);
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM sqlite_master
                WHERE type = 'table' AND name = $tableName
            );
            """;
        command.Parameters.AddWithValue("$tableName", tableName);
        return Convert.ToInt32(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static string ReadScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
                   command.ExecuteScalar(),
                   System.Globalization.CultureInfo.InvariantCulture)
               ?? string.Empty;
    }

    private static int ReadScalarInt32(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Checkpoint(SqliteConnection connection, string mode)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA wal_checkpoint({mode});";
        command.ExecuteNonQuery();
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

internal static class ValueTaskExtensions
{
    public static async ValueTask AsVoid<T>(this ValueTask<T> task)
    {
        _ = await task.ConfigureAwait(false);
    }
}

using BookStudio.Infrastructure.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace BookStudio.Tests.Integration;

internal static class SqliteJourney
{
    public static async Task RunAsync(string workspaceRoot)
    {
        RequireThrows<ArgumentException>(
            () => SqliteWorkspaceOptions.Create(workspaceRoot, "../escape.db"));

        var migrations = new SqliteMigrationCatalog().Load();
        var expectedMigrations = migrations.Count;
        var expectedLatestVersion = migrations.Max(item => item.Version);
        var options = SqliteWorkspaceOptions.Create(
            workspaceRoot,
            databaseFileName: "bookstudio.db",
            busyTimeoutSeconds: 5,
            writeQueueCapacity: 16);
        var database = new SqliteWorkspaceDatabase(options);

        var missingHealth = await database.CheckHealthAsync();
        Require(!missingHealth.Exists, "A new workspace must not report an existing database.");

        var initializedHealth = await database.InitializeAsync();
        AssertHealthy(initializedHealth, expectedMigrations, expectedLatestVersion);

        var secondInitialization = await database.InitializeAsync();
        AssertHealthy(secondInitialization, expectedMigrations, expectedLatestVersion);

        var connectionFactory = new SqliteConnectionFactory(options);
        using (var connection = connectionFactory.OpenConnection())
        {
            using var foreignKeysCommand = connection.CreateCommand();
            foreignKeysCommand.CommandText = "PRAGMA foreign_keys;";
            Require(
                Convert.ToInt32(foreignKeysCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1,
                "Foreign keys must be enabled on every connection.");

            using var timeoutCommand = connection.CreateCommand();
            timeoutCommand.CommandText = "PRAGMA busy_timeout;";
            Require(
                Convert.ToInt32(timeoutCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 5000,
                "Busy timeout must be 5000 milliseconds.");
        }

        var writes = Enumerable.Range(0, 64)
            .Select(index => database
                .SetMetadataAsync($"key-{index:D3}", $"value-{index:D3}")
                .AsTask())
            .ToArray();
        await Task.WhenAll(writes);
        Require(await database.CountMetadataAsync() == 64, "All serialized writes must commit exactly once.");
        Require(await database.GetMetadataAsync("key-063") == "value-063", "Metadata readback failed.");

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await RequireThrowsAsync<OperationCanceledException>(
                async () => await database.SetMetadataAsync(
                    "cancelled-key",
                    "must-not-commit",
                    cancellation.Token));
        }
        Require(await database.CountMetadataAsync() == 64, "A cancelled write must not commit.");

        await using (var rollbackQueue = new SqliteWriteQueue(connectionFactory, capacity: 4))
        {
            await RequireThrowsAsync<InvalidOperationException>(
                async () => await rollbackQueue.ExecuteInTransactionAsync<bool>(
                    (connection, transaction, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        using var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText = """
                            INSERT INTO workspace_metadata(key, value, updated_at_utc)
                            VALUES ('rollback-key', 'must-not-commit', 'test');
                            """;
                        command.ExecuteNonQuery();
                        throw new InvalidOperationException("force rollback");
                    }).AsTask());
        }
        Require(
            await database.GetMetadataAsync("rollback-key") is null,
            "A failed write operation must roll back its transaction.");

        await RequireThrowsAsync<ArgumentException>(
            async () => await database.BackupAsync(options.DatabasePath));
        var outsideBackup = Path.Combine(
            Directory.GetParent(workspaceRoot)?.FullName ?? Path.GetTempPath(),
            "outside-backup.db");
        await RequireThrowsAsync<ArgumentException>(
            async () => await database.BackupAsync(outsideBackup));

        var backupPath = Path.Combine(workspaceRoot, "backup.db");
        await database.BackupAsync(backupPath);
        Require(File.Exists(backupPath), "The online backup file must exist.");

        var backupOptions = SqliteWorkspaceOptions.Create(workspaceRoot, "backup.db");
        await using (var backupDatabase = new SqliteWorkspaceDatabase(backupOptions))
        {
            var backupHealth = await backupDatabase.InitializeAsync();
            AssertHealthy(backupHealth, expectedMigrations, expectedLatestVersion);
            Require(
                await backupDatabase.CountMetadataAsync() == 64,
                "The backup must contain every committed metadata row.");
        }

        var tamperOptions = SqliteWorkspaceOptions.Create(workspaceRoot, "tamper.db");
        await using (var tamperDatabase = new SqliteWorkspaceDatabase(tamperOptions))
        {
            _ = await tamperDatabase.InitializeAsync();
            var tamperFactory = new SqliteConnectionFactory(tamperOptions);
            using (var connection = tamperFactory.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE schema_migrations SET sha256 = 'corrupted' WHERE version = 1;";
                Require(command.ExecuteNonQuery() == 1, "Migration ledger tamper setup failed.");
            }
            await RequireThrowsAsync<InvalidOperationException>(
                async () => _ = await tamperDatabase.InitializeAsync());
        }

        await database.DisposeAsync();
        await RequireThrowsAsync<ObjectDisposedException>(
            async () => await database.SetMetadataAsync("after-dispose", "rejected"));
    }

    private static void AssertHealthy(
        BookStudio.Application.Persistence.WorkspaceDatabaseHealth health,
        int expectedMigrations,
        int expectedLatestVersion)
    {
        Require(health.Exists, "Database file must exist after initialization.");
        Require(health.IsHealthy, $"Database health failed: {health}.");
        Require(
            string.Equals(health.JournalMode, "wal", StringComparison.OrdinalIgnoreCase),
            "Journal mode must be WAL.");
        Require(health.ForeignKeysEnabled, "Foreign keys must be enabled.");
        Require(
            health.AppliedMigrationCount == expectedMigrations,
            $"Expected {expectedMigrations} applied migrations, actual {health.AppliedMigrationCount}.");
        Require(
            health.LatestMigrationVersion == expectedLatestVersion,
            "Latest migration version mismatch.");
        Require(
            string.Equals(health.IntegrityResult, "ok", StringComparison.OrdinalIgnoreCase),
            "PRAGMA quick_check must return ok.");
        Require(health.FileLengthBytes > 0, "Database file must not be empty.");
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> operation)
        where TException : Exception
    {
        try
        {
            await operation();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected exception {typeof(TException).Name} was not thrown.");
    }

    private static void RequireThrows<TException>(Action operation)
        where TException : Exception
    {
        try
        {
            operation();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected exception {typeof(TException).Name} was not thrown.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

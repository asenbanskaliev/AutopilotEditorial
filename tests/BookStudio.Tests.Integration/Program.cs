using BookStudio.Infrastructure.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

var workspaceRoot = Path.Combine(
    Path.GetTempPath(),
    "BookStudio.Tests.Integration",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspaceRoot);

var errors = new List<string>();

try
{
    await RunSqliteJourneyAsync(workspaceRoot);
}
catch (Exception exception)
{
    errors.Add(exception.ToString());
}
finally
{
    SqliteConnection.ClearAllPools();
    TryDeleteDirectory(workspaceRoot);
}

if (errors.Count == 0)
{
    Console.WriteLine("SQLite integration PASS: lifecycle, migrations, WAL, serialized writes, integrity and backup verified.");
    return 0;
}

foreach (var error in errors)
{
    Console.Error.WriteLine("SQLite integration FAIL: " + error);
}

return 1;

static async Task RunSqliteJourneyAsync(string workspaceRoot)
{
    var options = SqliteWorkspaceOptions.Create(
        workspaceRoot,
        databaseFileName: "bookstudio.db",
        busyTimeoutSeconds: 5,
        writeQueueCapacity: 16);
    var database = new SqliteWorkspaceDatabase(options);

    var missingHealth = await database.CheckHealthAsync();
    Require(!missingHealth.Exists, "A new workspace must not report an existing database.");

    var initializedHealth = await database.InitializeAsync();
    AssertHealthy(initializedHealth, expectedMigrations: 1);

    var secondInitialization = await database.InitializeAsync();
    AssertHealthy(secondInitialization, expectedMigrations: 1);
    Require(
        secondInitialization.LatestMigrationVersion == 1,
        "Repeated initialization must not duplicate migrations.");

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
    for (var index = 0; index < 64; index++)
    {
        var value = await database.GetMetadataAsync($"key-{index:D3}");
        Require(value == $"value-{index:D3}", $"Unexpected metadata value for key {index}.");
    }

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

    await RequireThrowsAsync<ArgumentException>(
        async () => await database.BackupAsync(options.DatabasePath));

    var backupPath = Path.Combine(workspaceRoot, "backup.db");
    await database.BackupAsync(backupPath);
    Require(File.Exists(backupPath), "The online backup file must exist.");

    var backupOptions = SqliteWorkspaceOptions.Create(workspaceRoot, "backup.db");
    await using (var backupDatabase = new SqliteWorkspaceDatabase(backupOptions))
    {
        var backupHealth = await backupDatabase.InitializeAsync();
        AssertHealthy(backupHealth, expectedMigrations: 1);
        Require(
            await backupDatabase.CountMetadataAsync() == 64,
            "The backup must contain every committed metadata row.");
        Require(
            await backupDatabase.GetMetadataAsync("key-063") == "value-063",
            "The backup must preserve metadata values.");
    }

    await database.DisposeAsync();
    await RequireThrowsAsync<ObjectDisposedException>(
        async () => await database.SetMetadataAsync("after-dispose", "rejected"));
}

static void AssertHealthy(
    BookStudio.Application.Persistence.WorkspaceDatabaseHealth health,
    int expectedMigrations)
{
    Require(health.Exists, "Database file must exist after initialization.");
    Require(health.IsHealthy, $"Database health failed: {health}.");
    Require(
        string.Equals(health.JournalMode, "wal", StringComparison.OrdinalIgnoreCase),
        "Journal mode must be WAL.");
    Require(health.ForeignKeysEnabled, "Foreign keys must be enabled.");
    Require(
        health.AppliedMigrationCount == expectedMigrations,
        $"Expected {expectedMigrations} applied migration(s), actual {health.AppliedMigrationCount}.");
    Require(
        string.Equals(health.IntegrityResult, "ok", StringComparison.OrdinalIgnoreCase),
        "PRAGMA quick_check must return ok.");
    Require(health.FileLengthBytes > 0, "Database file must not be empty.");
}

static async Task RequireThrowsAsync<TException>(Func<Task> operation)
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

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch (IOException)
    {
        // Cleanup is best effort after all pooled connections have been cleared.
    }
    catch (UnauthorizedAccessException)
    {
        // Cleanup is best effort after all pooled connections have been cleared.
    }
}

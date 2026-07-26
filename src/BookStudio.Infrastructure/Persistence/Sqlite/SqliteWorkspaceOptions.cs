namespace BookStudio.Infrastructure.Persistence.Sqlite;

/// <summary>Validated configuration for one local SQLite workspace database.</summary>
public sealed record SqliteWorkspaceOptions
{
    private SqliteWorkspaceOptions(
        string workspaceRoot,
        string databasePath,
        int busyTimeoutSeconds,
        int writeQueueCapacity)
    {
        WorkspaceRoot = workspaceRoot;
        DatabasePath = databasePath;
        BusyTimeoutSeconds = busyTimeoutSeconds;
        WriteQueueCapacity = writeQueueCapacity;
    }

    public string WorkspaceRoot { get; }

    public string DatabasePath { get; }

    public int BusyTimeoutSeconds { get; }

    public int WriteQueueCapacity { get; }

    public static SqliteWorkspaceOptions Create(
        string workspaceRoot,
        string databaseFileName = "bookstudio.db",
        int busyTimeoutSeconds = 5,
        int writeQueueCapacity = 256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseFileName);

        if (Path.IsPathRooted(databaseFileName) ||
            !string.Equals(Path.GetFileName(databaseFileName), databaseFileName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The database file name must be a simple relative file name.",
                nameof(databaseFileName));
        }

        if (busyTimeoutSeconds is < 1 or > 300)
        {
            throw new ArgumentOutOfRangeException(
                nameof(busyTimeoutSeconds),
                "Busy timeout must be between 1 and 300 seconds.");
        }

        if (writeQueueCapacity is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(writeQueueCapacity),
                "Write queue capacity must be between 1 and 100000.");
        }

        var canonicalRoot = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var databasePath = Path.GetFullPath(Path.Combine(canonicalRoot, databaseFileName));
        var rootPrefix = canonicalRoot + Path.DirectorySeparatorChar;

        if (!databasePath.StartsWith(rootPrefix, PathComparison))
        {
            throw new InvalidOperationException("The database path escapes the workspace root.");
        }

        return new SqliteWorkspaceOptions(
            canonicalRoot,
            databasePath,
            busyTimeoutSeconds,
            writeQueueCapacity);
    }

    internal static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}

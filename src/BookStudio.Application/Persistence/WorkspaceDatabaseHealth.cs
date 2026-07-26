namespace BookStudio.Application.Persistence;

/// <summary>Provider-neutral health information for the workspace database.</summary>
public sealed record WorkspaceDatabaseHealth(
    bool Exists,
    string JournalMode,
    bool ForeignKeysEnabled,
    int AppliedMigrationCount,
    int? LatestMigrationVersion,
    string IntegrityResult,
    long FileLengthBytes)
{
    public bool IsHealthy =>
        Exists &&
        string.Equals(JournalMode, "wal", StringComparison.OrdinalIgnoreCase) &&
        ForeignKeysEnabled &&
        string.Equals(IntegrityResult, "ok", StringComparison.OrdinalIgnoreCase);
}

using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite;

/// <summary>Applies embedded migrations once and detects changed applied SQL.</summary>
public sealed class SqliteMigrationRunner
{
    private readonly IReadOnlyList<SqliteMigration> _migrations;

    public SqliteMigrationRunner(SqliteMigrationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _migrations = catalog.Load();
    }

    public IReadOnlyList<SqliteMigration> Migrations => _migrations;

    public void ApplyPending(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        EnsureLedger(connection);

        var applied = ReadApplied(connection);
        foreach (var migration in _migrations)
        {
            if (applied.TryGetValue(migration.Version, out var appliedHash))
            {
                if (!string.Equals(appliedHash, migration.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Applied migration {migration.Id} has changed. " +
                        $"Database hash {appliedHash}, embedded hash {migration.Sha256}.");
                }

                continue;
            }

            using var transaction = connection.BeginTransaction();
            using (var migrationCommand = connection.CreateCommand())
            {
                migrationCommand.Transaction = transaction;
                migrationCommand.CommandText = migration.Sql;
                migrationCommand.ExecuteNonQuery();
            }

            using (var ledgerCommand = connection.CreateCommand())
            {
                ledgerCommand.Transaction = transaction;
                ledgerCommand.CommandText = """
                    INSERT INTO schema_migrations(version, name, sha256, applied_at_utc)
                    VALUES ($version, $name, $sha256, $appliedAtUtc);
                    """;
                ledgerCommand.Parameters.AddWithValue("$version", migration.Version);
                ledgerCommand.Parameters.AddWithValue("$name", migration.Name);
                ledgerCommand.Parameters.AddWithValue("$sha256", migration.Sha256);
                ledgerCommand.Parameters.AddWithValue(
                    "$appliedAtUtc",
                    DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                ledgerCommand.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    private static void EnsureLedger(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                applied_at_utc TEXT NOT NULL
            ) STRICT;
            """;
        command.ExecuteNonQuery();
    }

    private static Dictionary<int, string> ReadApplied(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, sha256 FROM schema_migrations ORDER BY version;";
        using var reader = command.ExecuteReader();

        var result = new Dictionary<int, string>();
        while (reader.Read())
        {
            result.Add(reader.GetInt32(0), reader.GetString(1));
        }

        return result;
    }
}

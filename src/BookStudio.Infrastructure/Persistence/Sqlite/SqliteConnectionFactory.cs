using Microsoft.Data.Sqlite;

namespace BookStudio.Infrastructure.Persistence.Sqlite;

/// <summary>Creates consistently configured SQLite connections.</summary>
public sealed class SqliteConnectionFactory
{
    private readonly SqliteWorkspaceOptions _options;
    private readonly string _connectionString;

    public SqliteConnectionFactory(SqliteWorkspaceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = options.BusyTimeoutSeconds,
        };

        _connectionString = builder.ToString();
    }

    public SqliteConnection OpenConnection()
    {
        Directory.CreateDirectory(_options.WorkspaceRoot);

        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = {_options.BusyTimeoutSeconds * 1000};
            PRAGMA synchronous = NORMAL;
            """;
        command.ExecuteNonQuery();

        return connection;
    }
}

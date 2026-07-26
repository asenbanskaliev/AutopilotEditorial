namespace BookStudio.Infrastructure.Persistence.Sqlite;

/// <summary>A deterministic embedded SQL migration.</summary>
public sealed record SqliteMigration(
    int Version,
    string Name,
    string Sql,
    string Sha256)
{
    public string Id => $"{Version:D4}_{Name}";
}

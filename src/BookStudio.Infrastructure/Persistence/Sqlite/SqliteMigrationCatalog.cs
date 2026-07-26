using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BookStudio.Infrastructure.Persistence.Sqlite;

/// <summary>Loads versioned SQL migrations embedded in the Infrastructure assembly.</summary>
public sealed partial class SqliteMigrationCatalog
{
    private readonly Assembly _assembly;

    public SqliteMigrationCatalog()
        : this(typeof(SqliteMigrationCatalog).Assembly)
    {
    }

    internal SqliteMigrationCatalog(Assembly assembly)
    {
        _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
    }

    public IReadOnlyList<SqliteMigration> Load()
    {
        var migrations = new List<SqliteMigration>();

        foreach (var resourceName in _assembly
                     .GetManifestResourceNames()
                     .Order(StringComparer.Ordinal))
        {
            var match = MigrationResourcePattern().Match(resourceName);
            if (!match.Success)
            {
                continue;
            }

            using var stream = _assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Migration resource cannot be opened: {resourceName}");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var sql = reader.ReadToEnd();

            var version = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (version <= 0)
            {
                throw new InvalidOperationException($"Migration version must be positive: {resourceName}");
            }

            var name = match.Groups[2].Value;
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
            migrations.Add(new SqliteMigration(version, name, sql, hash));
        }

        var duplicateVersions = migrations
            .GroupBy(migration => migration.Version)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicateVersions.Count > 0)
        {
            throw new InvalidOperationException(
                "Duplicate SQLite migration versions: " + string.Join(", ", duplicateVersions));
        }

        if (migrations.Count == 0)
        {
            throw new InvalidOperationException("No embedded SQLite migrations were found.");
        }

        return migrations.OrderBy(migration => migration.Version).ToList();
    }

    [GeneratedRegex(@"\.(\d{4})_([A-Za-z0-9_]+)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationResourcePattern();
}

using System.Security.Cryptography;
using System.Text;
using BookStudio.Mcp.BookCore;

namespace BookStudio.Mcp.Production;

/// <summary>Stable active and reserved identifiers for the bounded book-production server.</summary>
public static class BookProductionToolCatalog
{
    public const string ReleasePrepare = "book.release.prepare";
    public const string PreflightRun = "book.preflight.run";

    public static IReadOnlySet<string> ReservedToolNames { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "book.asset.register",
            "book.render.preview",
            "book.render.final",
            "book.publish.package",
        };

    private static readonly McpToolExecution SynchronousExecution = new("forbidden");

    public static McpToolDefinition PrepareTool { get; } = new(
        ReleasePrepare,
        "Prepare immutable release manifest",
        "Verify source artifacts and publish one canonical immutable release manifest without rendering or copying source bytes.",
        BookProductionSchemas.Parse(BookProductionSchemas.PrepareInputJson),
        BookProductionSchemas.Parse(BookProductionSchemas.PrepareOutputJson),
        new McpToolAnnotations(false, false, false, false),
        SynchronousExecution);

    public static McpToolDefinition PreflightTool { get; } = new(
        PreflightRun,
        "Run release preflight",
        "Verify an immutable release manifest and all source artifacts against the deterministic release-basic profile without modifying them.",
        BookProductionSchemas.Parse(BookProductionSchemas.PreflightInputJson),
        BookProductionSchemas.Parse(BookProductionSchemas.PreflightOutputJson),
        new McpToolAnnotations(true, false, true, false),
        SynchronousExecution);

    public static IReadOnlyList<McpToolDefinition> ActiveTools { get; } =
        new[] { ReleasePrepare, PreflightRun }
            .Select(name => name == ReleasePrepare ? PrepareTool : PreflightTool)
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();

    public static IReadOnlyDictionary<string, McpToolDefinition> ActiveByName { get; } =
        ActiveTools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

    public static IReadOnlyList<McpResourceDefinition> Resources { get; } =
        BookProductionSchemas.ResourceDocuments.Keys
            .OrderBy(uri => uri, StringComparer.Ordinal)
            .Select(uri => new McpResourceDefinition(
                uri,
                uri[(uri.LastIndexOf('/') + 1)..],
                Title(uri),
                uri.StartsWith("book://production/profiles/", StringComparison.Ordinal)
                    ? "Deterministic BookStudio production preflight profile."
                    : "Canonical JSON Schema for the verified BookStudio book-production MCP surface.",
                uri.StartsWith("book://production/profiles/", StringComparison.Ordinal)
                    ? "application/json"
                    : "application/schema+json"))
            .ToArray();

    public static string ToolCatalogFingerprint { get; } = Fingerprint(ActiveTools.Select(tool => tool.Name));
    public static string ResourceCatalogFingerprint { get; } = Fingerprint(Resources.Select(resource => resource.Uri));

    private static string Title(string uri)
    {
        var slug = uri[(uri.LastIndexOf('/') + 1)..];
        return string.Join(' ', slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static string Fingerprint(IEnumerable<string> identifiers)
    {
        var canonical = string.Join('\n', identifiers);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant()[..16];
    }
}

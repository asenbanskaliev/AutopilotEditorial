using System.Security.Cryptography;
using System.Text;
using BookStudio.Mcp.BookCore;

namespace BookStudio.Mcp.Quality;

/// <summary>Stable active and reserved identifiers for the bounded book-quality server.</summary>
public static class BookQualityToolCatalog
{
    public const string AuditRun = "book.audit.run";
    public const string GateEvaluate = "book.gate.evaluate";

    public static IReadOnlySet<string> ReservedToolNames { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "book.repair.propose",
            "book.repair.apply",
            "book.memory.get",
            "book.memory.commit",
        };

    private static readonly McpToolAnnotations ReadOnlyAnnotations = new(
        ReadOnlyHint: true,
        DestructiveHint: false,
        IdempotentHint: true,
        OpenWorldHint: false);

    private static readonly McpToolExecution SynchronousExecution = new("forbidden");

    public static McpToolDefinition AuditTool { get; } = new(
        AuditRun,
        "Run deterministic quality audit",
        "Integrity-check one immutable draft and return bounded deterministic metrics and quality checks without modifying it.",
        BookQualitySchemas.Parse(BookQualitySchemas.AuditInputJson),
        BookQualitySchemas.Parse(BookQualitySchemas.AuditOutputJson),
        ReadOnlyAnnotations,
        SynchronousExecution);

    public static McpToolDefinition GateTool { get; } = new(
        GateEvaluate,
        "Evaluate deterministic quality gate",
        "Evaluate the draft-basic profile and return PASS or BLOCKED with stable blocking reasons without persisting approval state.",
        BookQualitySchemas.Parse(BookQualitySchemas.GateInputJson),
        BookQualitySchemas.Parse(BookQualitySchemas.GateOutputJson),
        ReadOnlyAnnotations,
        SynchronousExecution);

    public static IReadOnlyList<McpToolDefinition> ActiveTools { get; } =
        new[] { AuditTool, GateTool }
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();

    public static IReadOnlyDictionary<string, McpToolDefinition> ActiveByName { get; } =
        ActiveTools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

    public static IReadOnlyList<McpResourceDefinition> Resources { get; } =
        BookQualitySchemas.ResourceDocuments.Keys
            .OrderBy(uri => uri, StringComparer.Ordinal)
            .Select(uri => new McpResourceDefinition(
                uri,
                uri[(uri.LastIndexOf('/') + 1)..],
                Title(uri),
                uri.StartsWith("book://quality/profiles/", StringComparison.Ordinal)
                    ? "Deterministic BookStudio quality profile."
                    : "Canonical JSON Schema for the verified BookStudio book-quality MCP surface.",
                uri.StartsWith("book://quality/profiles/", StringComparison.Ordinal)
                    ? "application/json"
                    : "application/schema+json"))
            .ToArray();

    public static string ToolCatalogFingerprint { get; } = Fingerprint(
        ActiveTools.Select(tool => tool.Name));

    public static string ResourceCatalogFingerprint { get; } = Fingerprint(
        Resources.Select(resource => resource.Uri));

    private static string Title(string uri)
    {
        var slug = uri[(uri.LastIndexOf('/') + 1)..];
        return string.Join(' ', slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static string Fingerprint(IEnumerable<string> identifiers)
    {
        var canonical = string.Join('\n', identifiers);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant()[..16];
    }
}

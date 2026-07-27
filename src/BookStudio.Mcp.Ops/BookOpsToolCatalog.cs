using System.Security.Cryptography;
using System.Text;
using BookStudio.Mcp.BookCore;

namespace BookStudio.Mcp.Ops;

/// <summary>Stable active and reserved identifiers for the bounded book-ops server.</summary>
public static class BookOpsToolCatalog
{
    public const string OpsStatus = "book.ops.status";
    public const string OpsDiagnostics = "book.ops.diagnostics";

    public static IReadOnlySet<string> ReservedToolNames { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "book.autopilot.start",
            "book.autopilot.status",
            "book.autopilot.pause",
            "book.autopilot.resume",
            "book.autopilot.cancel",
            "book.autopilot.replay",
        };

    private static readonly McpToolAnnotations ReadOnlyAnnotations = new(
        ReadOnlyHint: true,
        DestructiveHint: false,
        IdempotentHint: true,
        OpenWorldHint: false);

    private static readonly McpToolExecution SynchronousExecution = new("forbidden");

    public static McpToolDefinition StatusTool { get; } = new(
        OpsStatus,
        "Read operations status",
        "Run configured readiness probes and return a sanitized operational status without initializing, repairing or modifying the workspace.",
        BookOpsSchemas.Parse(BookOpsSchemas.EmptyInputJson),
        BookOpsSchemas.Parse(BookOpsSchemas.StatusOutputJson),
        ReadOnlyAnnotations,
        SynchronousExecution);

    public static McpToolDefinition DiagnosticsTool { get; } = new(
        OpsDiagnostics,
        "Run operations diagnostics",
        "Return sanitized readiness checks, the canonical product capability catalog and stable recommendations without changing durable state.",
        BookOpsSchemas.Parse(BookOpsSchemas.EmptyInputJson),
        BookOpsSchemas.Parse(BookOpsSchemas.DiagnosticsOutputJson),
        ReadOnlyAnnotations,
        SynchronousExecution);

    public static IReadOnlyList<McpToolDefinition> ActiveTools { get; } =
        new[] { StatusTool, DiagnosticsTool }
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();

    public static IReadOnlyDictionary<string, McpToolDefinition> ActiveByName { get; } =
        ActiveTools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

    public static IReadOnlyList<McpResourceDefinition> Resources { get; } =
        BookOpsSchemas.ResourceDocuments.Keys
            .OrderBy(uri => uri, StringComparer.Ordinal)
            .Select(uri => new McpResourceDefinition(
                uri,
                uri[(uri.LastIndexOf('/') + 1)..],
                Title(uri),
                uri == "book://ops/capabilities"
                    ? "Canonical availability catalog for implemented and reserved BookStudio capabilities."
                    : "Canonical JSON Schema for the verified BookStudio book-ops MCP surface.",
                uri == "book://ops/capabilities"
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

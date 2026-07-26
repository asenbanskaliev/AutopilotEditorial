using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookStudio.Mcp.BookCore;

public sealed record McpToolAnnotations(
    [property: JsonPropertyName("readOnlyHint")] bool ReadOnlyHint,
    [property: JsonPropertyName("destructiveHint")] bool DestructiveHint,
    [property: JsonPropertyName("idempotentHint")] bool IdempotentHint,
    [property: JsonPropertyName("openWorldHint")] bool OpenWorldHint);

public sealed record McpToolExecution(
    [property: JsonPropertyName("taskSupport")] string TaskSupport);

public sealed record McpToolDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("inputSchema")] JsonElement InputSchema,
    [property: JsonPropertyName("outputSchema")] JsonElement OutputSchema,
    [property: JsonPropertyName("annotations")] McpToolAnnotations Annotations,
    [property: JsonPropertyName("execution")] McpToolExecution Execution);

public sealed record McpResourceDefinition(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("mimeType")] string MimeType);

public sealed record McpResourceTemplateDefinition(
    [property: JsonPropertyName("uriTemplate")] string UriTemplate,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("mimeType")] string MimeType);

/// <summary>Stable public and reserved identifiers for the bounded book-core server.</summary>
public static class BookCoreToolCatalog
{
    public const string ArtifactGet = "book.artifact.get";
    public const string ArtifactCompare = "book.artifact.compare";

    public static IReadOnlySet<string> ReservedToolNames { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "book.project.create",
            "book.project.get_status",
            "book.project.configure",
            "book.decision.submit",
        };

    private static readonly McpToolAnnotations ReadOnlyAnnotations = new(
        ReadOnlyHint: true,
        DestructiveHint: false,
        IdempotentHint: true,
        OpenWorldHint: false);

    private static readonly McpToolExecution SynchronousExecution = new("forbidden");

    public static IReadOnlyList<McpToolDefinition> ActiveTools { get; } =
        new[]
        {
            new McpToolDefinition(
                ArtifactCompare,
                "Compare artifact versions",
                "Compare two immutable text-compatible artifact versions with a bounded deterministic line diff.",
                BookCoreSchemas.Parse(BookCoreSchemas.ArtifactCompareInputJson),
                BookCoreSchemas.Parse(BookCoreSchemas.ArtifactCompareOutputJson),
                ReadOnlyAnnotations,
                SynchronousExecution),
            new McpToolDefinition(
                ArtifactGet,
                "Get artifact version",
                "Read immutable artifact metadata and optionally inline bounded UTF-8 text without exposing filesystem paths.",
                BookCoreSchemas.Parse(BookCoreSchemas.ArtifactGetInputJson),
                BookCoreSchemas.Parse(BookCoreSchemas.ArtifactGetOutputJson),
                ReadOnlyAnnotations,
                SynchronousExecution),
        }
        .OrderBy(tool => tool.Name, StringComparer.Ordinal)
        .ToArray();

    public static IReadOnlyDictionary<string, McpToolDefinition> ActiveByName { get; } =
        ActiveTools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

    public static IReadOnlyList<McpResourceDefinition> SchemaResources { get; } =
        BookCoreSchemas.ResourceSchemas.Keys
            .OrderBy(uri => uri, StringComparer.Ordinal)
            .Select(uri => new McpResourceDefinition(
                uri,
                uri[(uri.LastIndexOf('/') + 1)..],
                SchemaTitle(uri),
                "Canonical JSON Schema for the verified BookStudio book-core MCP surface.",
                "application/schema+json"))
            .ToArray();

    public static IReadOnlyList<McpResourceTemplateDefinition> ResourceTemplates { get; } =
    [
        new McpResourceTemplateDefinition(
            "book://project/{projectId}/artifact/{artifactId}/versions/{version}",
            "artifact-version",
            "Immutable artifact version",
            "Read one project-confined immutable artifact version after integrity verification.",
            "application/octet-stream"),
    ];

    public static string ToolCatalogFingerprint { get; } = Fingerprint(
        ActiveTools.Select(tool => tool.Name));

    public static string ResourceCatalogFingerprint { get; } = Fingerprint(
        SchemaResources.Select(resource => resource.Uri));

    public static string TemplateCatalogFingerprint { get; } = Fingerprint(
        ResourceTemplates.Select(template => template.UriTemplate));

    private static string SchemaTitle(string uri)
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

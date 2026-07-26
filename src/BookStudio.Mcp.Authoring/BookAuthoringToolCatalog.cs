using System.Security.Cryptography;
using System.Text;
using BookStudio.Mcp.BookCore;

namespace BookStudio.Mcp.Authoring;

/// <summary>Stable active and reserved identifiers for the bounded book-authoring server.</summary>
public static class BookAuthoringToolCatalog
{
    public const string DraftRegister = "book.draft.register";
    public const string DraftValidate = "book.draft.validate";

    public static IReadOnlySet<string> ReservedToolNames { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "book.plan.create",
            "book.scene.generate",
            "book.chapter.generate",
            "book.manuscript.assemble",
        };

    private static readonly McpToolAnnotations RegisterAnnotations = new(
        ReadOnlyHint: false,
        DestructiveHint: false,
        IdempotentHint: false,
        OpenWorldHint: false);

    private static readonly McpToolAnnotations ValidateAnnotations = new(
        ReadOnlyHint: true,
        DestructiveHint: false,
        IdempotentHint: true,
        OpenWorldHint: false);

    private static readonly McpToolExecution SynchronousExecution = new("forbidden");

    public static McpToolDefinition RegisterTool { get; } = new(
        DraftRegister,
        "Register immutable draft version",
        "Publish one bounded UTF-8 draft version into the project-confined immutable Artifact Store.",
        BookAuthoringSchemas.Parse(BookAuthoringSchemas.DraftRegisterInputJson),
        BookAuthoringSchemas.Parse(BookAuthoringSchemas.DraftRegisterOutputJson),
        RegisterAnnotations,
        SynchronousExecution);

    public static McpToolDefinition ValidateTool { get; } = new(
        DraftValidate,
        "Validate stored draft",
        "Integrity-check a stored textual draft and return deterministic metrics and bounded warnings without modifying it.",
        BookAuthoringSchemas.Parse(BookAuthoringSchemas.DraftValidateInputJson),
        BookAuthoringSchemas.Parse(BookAuthoringSchemas.DraftValidateOutputJson),
        ValidateAnnotations,
        SynchronousExecution);

    public static IReadOnlyList<McpToolDefinition> ActiveTools { get; } =
        new[] { RegisterTool, ValidateTool }
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();

    public static IReadOnlyDictionary<string, McpToolDefinition> ActiveByName { get; } =
        ActiveTools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

    public static IReadOnlyList<McpResourceDefinition> SchemaResources { get; } =
        BookAuthoringSchemas.ResourceSchemas.Keys
            .OrderBy(uri => uri, StringComparer.Ordinal)
            .Select(uri => new McpResourceDefinition(
                uri,
                uri[(uri.LastIndexOf('/') + 1)..],
                SchemaTitle(uri),
                "Canonical JSON Schema for the verified BookStudio book-authoring MCP surface.",
                "application/schema+json"))
            .ToArray();

    public static IReadOnlyList<McpResourceTemplateDefinition> ResourceTemplates { get; } =
    [
        new McpResourceTemplateDefinition(
            "book://project/{projectId}/artifact/{artifactId}/versions/{version}",
            "draft-version",
            "Immutable authoring draft version",
            "Read one project-confined textual draft version after integrity verification.",
            "text/markdown"),
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

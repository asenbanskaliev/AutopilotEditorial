using System.Text.Json;
using System.Text.Json.Serialization;
using BookStudio.Application.Artifacts;
using BookStudio.Mcp.Protocol;

namespace BookStudio.Mcp.BookCore;

/// <summary>Executable read-only tools and resources for the verified book-core surface.</summary>
public sealed class BookCoreFeatureRouter : IMcpFeatureRouter, IAsyncDisposable
{
    private const int ToolPageSize = 50;
    private const int ResourcePageSize = 3;
    private const int TemplatePageSize = 20;
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

    private static readonly IReadOnlyDictionary<string, object> ServerCapabilities =
        new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["tools"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["listChanged"] = false,
            },
            ["resources"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["subscribe"] = false,
                ["listChanged"] = false,
            },
        };

    private readonly Lazy<IArtifactQueryService> _queries;
    private readonly Func<ValueTask>? _disposeAsync;
    private int _disposed;

    public BookCoreFeatureRouter(
        Func<IArtifactQueryService> queryServiceFactory,
        Func<ValueTask>? disposeAsync = null)
    {
        ArgumentNullException.ThrowIfNull(queryServiceFactory);
        _queries = new Lazy<IArtifactQueryService>(
            queryServiceFactory,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _disposeAsync = disposeAsync;
    }

    public IReadOnlyDictionary<string, object> Capabilities => ServerCapabilities;

    public string Instructions =>
        "BookStudio book-core is ready for immutable artifact reads and comparisons. Project and decision workflows are not exposed yet.";

    public async ValueTask<McpDispatchResult?> TryDispatchAsync(
        string method,
        JsonElement? parameters,
        JsonElement requestId,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return method switch
        {
            "tools/list" => HandleToolsList(parameters, requestId),
            "tools/call" => await HandleToolsCallAsync(parameters, requestId, cancellationToken)
                .ConfigureAwait(false),
            "resources/list" => HandleResourcesList(parameters, requestId),
            "resources/templates/list" => HandleResourceTemplatesList(parameters, requestId),
            "resources/read" => await HandleResourceReadAsync(parameters, requestId, cancellationToken)
                .ConfigureAwait(false),
            _ => null,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_disposeAsync is not null)
        {
            await _disposeAsync().ConfigureAwait(false);
        }
    }

    private static McpDispatchResult HandleToolsList(
        JsonElement? parameters,
        JsonElement requestId)
    {
        if (!TryReadCursor(
                parameters,
                "tools",
                BookCoreToolCatalog.ToolCatalogFingerprint,
                out var offset,
                out var error))
        {
            return InvalidParams(requestId, error!);
        }

        var page = Slice(
            BookCoreToolCatalog.ActiveTools,
            offset,
            ToolPageSize,
            "tools",
            BookCoreToolCatalog.ToolCatalogFingerprint);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tools"] = page.Items,
        };
        if (page.NextCursor is not null)
        {
            result["nextCursor"] = page.NextCursor;
        }
        return new McpDispatchResult(JsonRpcMessageWriter.Result(requestId, result));
    }

    private async ValueTask<McpDispatchResult> HandleToolsCallAsync(
        JsonElement? parameters,
        JsonElement requestId,
        CancellationToken cancellationToken)
    {
        if (parameters is null ||
            parameters.Value.ValueKind != JsonValueKind.Object ||
            !parameters.Value.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String ||
            (parameters.Value.TryGetProperty("arguments", out var parsedArguments) &&
             parsedArguments.ValueKind != JsonValueKind.Object))
        {
            return InvalidParams(
                requestId,
                "tools/call params require a string name and optional object arguments.");
        }

        var arguments = parameters.Value.TryGetProperty("arguments", out var providedArguments)
            ? providedArguments
            : EmptyObject;

        var toolName = nameElement.GetString();
        if (string.IsNullOrWhiteSpace(toolName) || toolName.Length > 128)
        {
            return InvalidParams(requestId, "Tool name is invalid.");
        }
        if (!BookCoreToolCatalog.ActiveByName.ContainsKey(toolName))
        {
            return InvalidParams(requestId, "Unknown tool.");
        }

        try
        {
            return toolName switch
            {
                BookCoreToolCatalog.ArtifactGet => await CallArtifactGetAsync(
                        arguments,
                        requestId,
                        cancellationToken)
                    .ConfigureAwait(false),
                BookCoreToolCatalog.ArtifactCompare => await CallArtifactCompareAsync(
                        arguments,
                        requestId,
                        cancellationToken)
                    .ConfigureAwait(false),
                _ => InvalidParams(requestId, "Unknown tool."),
            };
        }
        catch (ArtifactQueryException exception)
        {
            return ToolFailure(
                requestId,
                toolName,
                exception.Code,
                exception.Message);
        }
    }

    private async ValueTask<McpDispatchResult> CallArtifactGetAsync(
        JsonElement arguments,
        JsonElement requestId,
        CancellationToken cancellationToken)
    {
        if (!TryParseArtifactGet(arguments, out var query, out var error))
        {
            return ToolFailure(
                requestId,
                BookCoreToolCatalog.ArtifactGet,
                "invalid_arguments",
                error!);
        }

        var result = await _queries.Value.GetAsync(query!, cancellationToken)
            .ConfigureAwait(false);
        var operationId = ArtifactQueryService.BuildOperationId(
            BookCoreToolCatalog.ArtifactGet,
            result.Artifact.ArtifactId,
            result.Artifact.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            result.Artifact.Sha256,
            result.ContentIncluded ? "content" : "reference");
        var structured = new BookCoreStructuredResult(
            "complete",
            operationId,
            [result.Artifact],
            result.Warnings,
            new
            {
                artifact = result.Artifact,
                inlineText = result.InlineText,
                contentIncluded = result.ContentIncluded,
            },
            Error: null);
        var content = new List<object>
        {
            new McpTextContent(
                "text",
                result.ContentIncluded
                    ? $"Artifact {result.Artifact.ArtifactId} version {result.Artifact.Version} was read with bounded inline text."
                    : $"Artifact {result.Artifact.ArtifactId} version {result.Artifact.Version} is available as an immutable resource reference."),
            new McpResourceLinkContent(
                "resource_link",
                result.Artifact.Uri,
                result.Artifact.ArtifactId,
                $"Immutable artifact version {result.Artifact.Version}.",
                result.Artifact.MediaType),
        };
        return ToolSuccess(requestId, content, structured);
    }

    private async ValueTask<McpDispatchResult> CallArtifactCompareAsync(
        JsonElement arguments,
        JsonElement requestId,
        CancellationToken cancellationToken)
    {
        if (!TryParseArtifactCompare(arguments, out var query, out var error))
        {
            return ToolFailure(
                requestId,
                BookCoreToolCatalog.ArtifactCompare,
                "invalid_arguments",
                error!);
        }

        var result = await _queries.Value.CompareAsync(query!, cancellationToken)
            .ConfigureAwait(false);
        var operationId = ArtifactQueryService.BuildOperationId(
            BookCoreToolCatalog.ArtifactCompare,
            result.Left.ArtifactId,
            result.Left.Sha256,
            result.Right.Sha256,
            query!.MaxDifferences.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var structured = new BookCoreStructuredResult(
            "complete",
            operationId,
            [result.Left, result.Right],
            result.Warnings,
            new
            {
                left = result.Left,
                right = result.Right,
                identical = result.Identical,
                summary = result.Summary,
                differences = result.Differences,
            },
            Error: null);
        var summary = result.Identical
            ? "The artifact versions are content-identical."
            : result.Summary.TextDiffPerformed
                ? $"Artifact comparison found {result.Summary.AddedLines} added and {result.Summary.RemovedLines} removed lines."
                : "Artifact metadata differs; a bounded text diff was not available.";
        return ToolSuccess(
            requestId,
            [new McpTextContent("text", summary)],
            structured);
    }

    private static McpDispatchResult HandleResourcesList(
        JsonElement? parameters,
        JsonElement requestId)
    {
        if (!TryReadCursor(
                parameters,
                "resources",
                BookCoreToolCatalog.ResourceCatalogFingerprint,
                out var offset,
                out var error))
        {
            return InvalidParams(requestId, error!);
        }

        var page = Slice(
            BookCoreToolCatalog.SchemaResources,
            offset,
            ResourcePageSize,
            "resources",
            BookCoreToolCatalog.ResourceCatalogFingerprint);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["resources"] = page.Items,
        };
        if (page.NextCursor is not null)
        {
            result["nextCursor"] = page.NextCursor;
        }
        return new McpDispatchResult(JsonRpcMessageWriter.Result(requestId, result));
    }

    private static McpDispatchResult HandleResourceTemplatesList(
        JsonElement? parameters,
        JsonElement requestId)
    {
        if (!TryReadCursor(
                parameters,
                "resource-templates",
                BookCoreToolCatalog.TemplateCatalogFingerprint,
                out var offset,
                out var error))
        {
            return InvalidParams(requestId, error!);
        }

        var page = Slice(
            BookCoreToolCatalog.ResourceTemplates,
            offset,
            TemplatePageSize,
            "resource-templates",
            BookCoreToolCatalog.TemplateCatalogFingerprint);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["resourceTemplates"] = page.Items,
        };
        if (page.NextCursor is not null)
        {
            result["nextCursor"] = page.NextCursor;
        }
        return new McpDispatchResult(JsonRpcMessageWriter.Result(requestId, result));
    }

    private async ValueTask<McpDispatchResult> HandleResourceReadAsync(
        JsonElement? parameters,
        JsonElement requestId,
        CancellationToken cancellationToken)
    {
        if (parameters is null ||
            !HasOnlyProperties(parameters.Value, "uri") ||
            !parameters.Value.TryGetProperty("uri", out var uriElement) ||
            uriElement.ValueKind != JsonValueKind.String)
        {
            return InvalidParams(requestId, "resources/read params require exactly one string uri.");
        }

        var uriText = uriElement.GetString();
        if (string.IsNullOrWhiteSpace(uriText) || uriText.Length > 512)
        {
            return InvalidParams(requestId, "Resource URI is invalid.");
        }

        if (BookCoreSchemas.ResourceSchemas.TryGetValue(uriText, out var schema))
        {
            return new McpDispatchResult(
                JsonRpcMessageWriter.Result(
                    requestId,
                    new
                    {
                        contents = new[]
                        {
                            new McpResourceContent(
                                uriText,
                                "application/schema+json",
                                schema,
                                Blob: null),
                        },
                    }));
        }

        if (!TryParseArtifactResourceUri(uriText, out var query))
        {
            return InvalidParams(requestId, "Unknown resource URI.");
        }

        try
        {
            var resource = await _queries.Value.ReadResourceAsync(query!, cancellationToken)
                .ConfigureAwait(false);
            return new McpDispatchResult(
                JsonRpcMessageWriter.Result(
                    requestId,
                    new
                    {
                        contents = new[]
                        {
                            new McpResourceContent(
                                resource.Artifact.Uri,
                                resource.Artifact.MediaType,
                                resource.Text,
                                resource.BlobBase64),
                        },
                    }));
        }
        catch (ArtifactQueryException exception)
        {
            return new McpDispatchResult(
                JsonRpcMessageWriter.Error(
                    requestId,
                    JsonRpcErrorCodes.InvalidParams,
                    "Resource could not be read.",
                    new { code = exception.Code }),
                "MCP_RESOURCE_READ_FAILED");
        }
    }

    private static McpDispatchResult ToolSuccess(
        JsonElement requestId,
        IReadOnlyList<object> content,
        BookCoreStructuredResult structured) =>
        new(JsonRpcMessageWriter.Result(
            requestId,
            new McpCallToolResult(content, structured, IsError: false)));

    private static McpDispatchResult ToolFailure(
        JsonElement requestId,
        string toolName,
        string code,
        string safeMessage)
    {
        var structured = new BookCoreStructuredResult(
            "failed",
            ArtifactQueryService.BuildOperationId(toolName, code),
            [],
            [],
            new Dictionary<string, object>(StringComparer.Ordinal),
            new BookCoreError(code, BoundMessage(safeMessage)));
        return new McpDispatchResult(
            JsonRpcMessageWriter.Result(
                requestId,
                new McpCallToolResult(
                    [new McpTextContent("text", BoundMessage(safeMessage))],
                    structured,
                    IsError: true)),
            "MCP_TOOL_EXECUTION_FAILED");
    }

    private static McpDispatchResult InvalidParams(
        JsonElement requestId,
        string message) =>
        new(
            JsonRpcMessageWriter.Error(
                requestId,
                JsonRpcErrorCodes.InvalidParams,
                message),
            "MCP_INVALID_PARAMS");

    private static bool TryParseArtifactGet(
        JsonElement arguments,
        out ArtifactGetQuery? query,
        out string? error)
    {
        query = null;
        if (!TryReadCommonArguments(
                arguments,
                out var projectId,
                out var payload,
                out error))
        {
            return false;
        }
        if (!HasOnlyProperties(payload, "artifactId", "version", "includeContent") ||
            !TryReadString(payload, "artifactId", out var artifactId) ||
            !TryReadPositiveInt(payload, "version", out var version))
        {
            error = "artifact.get payload requires artifactId, positive version and optional boolean includeContent.";
            return false;
        }

        var includeContent = false;
        if (payload.TryGetProperty("includeContent", out var include))
        {
            if (include.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                error = "includeContent must be boolean.";
                return false;
            }
            includeContent = include.GetBoolean();
        }

        query = new ArtifactGetQuery(projectId!, artifactId!, version, includeContent);
        error = null;
        return true;
    }

    private static bool TryParseArtifactCompare(
        JsonElement arguments,
        out ArtifactCompareQuery? query,
        out string? error)
    {
        query = null;
        if (!TryReadCommonArguments(
                arguments,
                out var projectId,
                out var payload,
                out error))
        {
            return false;
        }
        if (!HasOnlyProperties(
                payload,
                "artifactId",
                "leftVersion",
                "rightVersion",
                "maxDifferences") ||
            !TryReadString(payload, "artifactId", out var artifactId) ||
            !TryReadPositiveInt(payload, "leftVersion", out var leftVersion) ||
            !TryReadPositiveInt(payload, "rightVersion", out var rightVersion))
        {
            error = "artifact.compare payload requires artifactId and two positive versions.";
            return false;
        }

        var maxDifferences = 20;
        if (payload.TryGetProperty("maxDifferences", out var maximum))
        {
            if (maximum.ValueKind != JsonValueKind.Number ||
                !maximum.TryGetInt32(out maxDifferences) ||
                maxDifferences is < 1 or > 100)
            {
                error = "maxDifferences must be an integer between 1 and 100.";
                return false;
            }
        }

        query = new ArtifactCompareQuery(
            projectId!,
            artifactId!,
            leftVersion,
            rightVersion,
            maxDifferences);
        error = null;
        return true;
    }

    private static bool TryReadCommonArguments(
        JsonElement arguments,
        out string? projectId,
        out JsonElement payload,
        out string? error)
    {
        projectId = null;
        payload = default;
        if (!HasOnlyProperties(arguments, "projectId", "payload") ||
            !TryReadString(arguments, "projectId", out projectId) ||
            !arguments.TryGetProperty("payload", out payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            error = "Tool arguments require exactly projectId and object payload.";
            return false;
        }
        error = null;
        return true;
    }

    private static bool TryParseArtifactResourceUri(
        string uriText,
        out ArtifactResourceQuery? query)
    {
        query = null;
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "book", StringComparison.Ordinal) ||
            !string.Equals(uri.Host, "project", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 5 ||
            !string.Equals(segments[1], "artifact", StringComparison.Ordinal) ||
            !string.Equals(segments[3], "versions", StringComparison.Ordinal) ||
            !int.TryParse(
                segments[4],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var version) ||
            version < 1)
        {
            return false;
        }

        query = new ArtifactResourceQuery(
            Uri.UnescapeDataString(segments[0]),
            Uri.UnescapeDataString(segments[2]),
            version);
        return true;
    }

    private static bool TryReadCursor(
        JsonElement? parameters,
        string scope,
        string fingerprint,
        out int offset,
        out string? error)
    {
        offset = 0;
        error = null;
        if (parameters is null)
        {
            return true;
        }
        if (!HasOnlyProperties(parameters.Value, "cursor"))
        {
            error = "List params may contain only cursor.";
            return false;
        }
        if (!parameters.Value.TryGetProperty("cursor", out var cursorElement))
        {
            return true;
        }
        if (cursorElement.ValueKind != JsonValueKind.String)
        {
            error = "cursor must be a string.";
            return false;
        }

        try
        {
            offset = McpCursorCodec.Decode(
                cursorElement.GetString() ?? string.Empty,
                scope,
                fingerprint);
            return true;
        }
        catch (McpCursorException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static CursorPage<T> Slice<T>(
        IReadOnlyList<T> source,
        int offset,
        int pageSize,
        string scope,
        string fingerprint)
    {
        if (offset > source.Count)
        {
            throw new McpCursorException("Cursor offset exceeds the catalog.");
        }
        var items = source.Skip(offset).Take(pageSize).ToArray();
        var nextOffset = offset + items.Length;
        var nextCursor = nextOffset < source.Count
            ? McpCursorCodec.Encode(scope, nextOffset, fingerprint)
            : null;
        return new CursorPage<T>(items, nextCursor);
    }

    private static bool HasOnlyProperties(
        JsonElement source,
        params string[] allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        return source.ValueKind == JsonValueKind.Object &&
               source.EnumerateObject().All(property => set.Contains(property.Name));
    }

    private static bool TryReadString(
        JsonElement source,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!source.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value) &&
               value.Length <= 256 &&
               value.All(character => !char.IsControl(character));
    }

    private static bool TryReadPositiveInt(
        JsonElement source,
        string propertyName,
        out int value)
    {
        value = 0;
        return source.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value) &&
               value > 0;
    }

    private static string BoundMessage(string message)
    {
        var sanitized = new string(message
            .Where(character => !char.IsControl(character))
            .Take(512)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized)
            ? "Tool execution failed."
            : sanitized;
    }

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private sealed record CursorPage<T>(
        IReadOnlyList<T> Items,
        string? NextCursor);
}

public sealed record McpTextContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

public sealed record McpResourceLinkContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("mimeType")] string MimeType);

public sealed record McpCallToolResult(
    [property: JsonPropertyName("content")] IReadOnlyList<object> Content,
    [property: JsonPropertyName("structuredContent")] BookCoreStructuredResult StructuredContent,
    [property: JsonPropertyName("isError")] bool IsError);

public sealed record BookCoreStructuredResult(
    [property: JsonPropertyName("resultType")] string ResultType,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("artifactRefs")] IReadOnlyList<ArtifactLogicalReference> ArtifactRefs,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("data")] object Data,
    [property: JsonPropertyName("error")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    BookCoreError? Error);

public sealed record BookCoreError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

public sealed record McpResourceContent(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("text")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Text,
    [property: JsonPropertyName("blob")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Blob);

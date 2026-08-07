using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookStudio.Application.Authoring;
using BookStudio.Mcp.BookCore;
using BookStudio.Mcp.Protocol;

namespace BookStudio.Mcp.Authoring;

/// <summary>Executable deterministic draft tools and resources for book-authoring.</summary>
public sealed class BookAuthoringFeatureRouter : IMcpFeatureRouter, IAsyncDisposable
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

    private readonly Lazy<IDraftAuthoringService> _service;
    private readonly Func<ValueTask>? _disposeAsync;
    private int _disposed;

    public BookAuthoringFeatureRouter(
        Func<IDraftAuthoringService> serviceFactory,
        Func<ValueTask>? disposeAsync = null)
    {
        ArgumentNullException.ThrowIfNull(serviceFactory);
        _service = new Lazy<IDraftAuthoringService>(
            serviceFactory,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _disposeAsync = disposeAsync;
    }

    public IReadOnlyDictionary<string, object> Capabilities => ServerCapabilities;

    public string Instructions =>
        "BookStudio authoring is ready to register immutable text drafts and validate stored draft versions. AI generation and planning tools are not exposed yet.";

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
                "authoring-tools",
                BookAuthoringToolCatalog.ToolCatalogFingerprint,
                out var offset,
                out var error))
        {
            return InvalidParams(requestId, error!);
        }

        var page = Slice(
            BookAuthoringToolCatalog.ActiveTools,
            offset,
            ToolPageSize,
            "authoring-tools",
            BookAuthoringToolCatalog.ToolCatalogFingerprint);
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
        if (string.IsNullOrWhiteSpace(toolName) ||
            toolName.Length > 128 ||
            !BookAuthoringToolCatalog.ActiveByName.ContainsKey(toolName))
        {
            return InvalidParams(requestId, "Unknown tool.");
        }

        try
        {
            return toolName switch
            {
                BookAuthoringToolCatalog.DraftRegister => await CallRegisterAsync(
                        arguments,
                        requestId,
                        cancellationToken)
                    .ConfigureAwait(false),
                BookAuthoringToolCatalog.DraftValidate => await CallValidateAsync(
                        arguments,
                        requestId,
                        cancellationToken)
                    .ConfigureAwait(false),
                _ => InvalidParams(requestId, "Unknown tool."),
            };
        }
        catch (DraftAuthoringException exception)
        {
            return ToolFailure(requestId, toolName, exception.Code, exception.Message);
        }
    }

    private async ValueTask<McpDispatchResult> CallRegisterAsync(
        JsonElement arguments,
        JsonElement requestId,
        CancellationToken cancellationToken)
    {
        if (!TryParseRegister(arguments, out var command, out var error))
        {
            return ToolFailure(
                requestId,
                BookAuthoringToolCatalog.DraftRegister,
                "invalid_arguments",
                error!);
        }

        var result = await _service.Value.RegisterAsync(command!, cancellationToken)
            .ConfigureAwait(false);
        var operationId = DraftAuthoringService.BuildOperationId(
            BookAuthoringToolCatalog.DraftRegister,
            result.Artifact.ArtifactId,
            result.Artifact.Version.ToString(CultureInfo.InvariantCulture),
            result.Artifact.Sha256);
        var structured = new AuthoringStructuredResult(
            "complete",
            operationId,
            [result.Artifact],
            result.Warnings,
            new { artifact = result.Artifact },
            Error: null);
        return ToolSuccess(
            requestId,
            [
                new McpTextContent(
                    "text",
                    $"Draft {result.Artifact.ArtifactId} version {result.Artifact.Version} was registered immutably."),
                new McpResourceLinkContent(
                    "resource_link",
                    result.Artifact.Uri,
                    result.Artifact.ArtifactId,
                    $"Immutable draft version {result.Artifact.Version}.",
                    result.Artifact.MediaType),
            ],
            structured);
    }

    private async ValueTask<McpDispatchResult> CallValidateAsync(
        JsonElement arguments,
        JsonElement requestId,
        CancellationToken cancellationToken)
    {
        if (!TryParseValidate(arguments, out var query, out var error))
        {
            return ToolFailure(
                requestId,
                BookAuthoringToolCatalog.DraftValidate,
                "invalid_arguments",
                error!);
        }

        var result = await _service.Value.ValidateAsync(query!, cancellationToken)
            .ConfigureAwait(false);
        var operationId = DraftAuthoringService.BuildOperationId(
            BookAuthoringToolCatalog.DraftValidate,
            result.Artifact.ArtifactId,
            result.Artifact.Version.ToString(CultureInfo.InvariantCulture),
            result.Artifact.Sha256,
            query!.MaximumLineLength.ToString(CultureInfo.InvariantCulture));
        var structured = new AuthoringStructuredResult(
            "complete",
            operationId,
            [result.Artifact],
            result.Warnings,
            new
            {
                artifact = result.Artifact,
                metrics = result.Metrics,
                isValid = result.IsValid,
            },
            Error: null);
        var summary = result.Warnings.Count == 0
            ? "Draft validation completed without warnings."
            : $"Draft validation completed with {result.Warnings.Count} warning categories.";
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
                "authoring-resources",
                BookAuthoringToolCatalog.ResourceCatalogFingerprint,
                out var offset,
                out var error))
        {
            return InvalidParams(requestId, error!);
        }

        var page = Slice(
            BookAuthoringToolCatalog.SchemaResources,
            offset,
            ResourcePageSize,
            "authoring-resources",
            BookAuthoringToolCatalog.ResourceCatalogFingerprint);
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
                "authoring-templates",
                BookAuthoringToolCatalog.TemplateCatalogFingerprint,
                out var offset,
                out var error))
        {
            return InvalidParams(requestId, error!);
        }

        var page = Slice(
            BookAuthoringToolCatalog.ResourceTemplates,
            offset,
            TemplatePageSize,
            "authoring-templates",
            BookAuthoringToolCatalog.TemplateCatalogFingerprint);
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

        if (BookAuthoringSchemas.ResourceSchemas.TryGetValue(uriText, out var schema))
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

        if (!TryParseDraftResourceUri(uriText, out var query))
        {
            return InvalidParams(requestId, "Unknown resource URI.");
        }

        try
        {
            var resource = await _service.Value.ReadResourceAsync(query!, cancellationToken)
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
                                Blob: null),
                        },
                    }));
        }
        catch (DraftAuthoringException exception)
        {
            return new McpDispatchResult(
                JsonRpcMessageWriter.Error(
                    requestId,
                    JsonRpcErrorCodes.InvalidParams,
                    "Draft resource could not be read.",
                    new { code = exception.Code }),
                "MCP_AUTHORING_RESOURCE_READ_FAILED");
        }
    }

    private static McpDispatchResult ToolSuccess(
        JsonElement requestId,
        IReadOnlyList<object> content,
        AuthoringStructuredResult structured) =>
        new(JsonRpcMessageWriter.Result(
            requestId,
            new AuthoringCallToolResult(content, structured, IsError: false)));

    private static McpDispatchResult ToolFailure(
        JsonElement requestId,
        string toolName,
        string code,
        string safeMessage)
    {
        var bounded = BoundMessage(safeMessage);
        var structured = new AuthoringStructuredResult(
            "failed",
            DraftAuthoringService.BuildOperationId(toolName, code),
            [],
            [],
            new Dictionary<string, object>(StringComparer.Ordinal),
            new AuthoringError(code, bounded));
        return new McpDispatchResult(
            JsonRpcMessageWriter.Result(
                requestId,
                new AuthoringCallToolResult(
                    [new McpTextContent("text", bounded)],
                    structured,
                    IsError: true)),
            "MCP_AUTHORING_TOOL_FAILED");
    }

    private static McpDispatchResult InvalidParams(JsonElement requestId, string message) =>
        new(
            JsonRpcMessageWriter.Error(
                requestId,
                JsonRpcErrorCodes.InvalidParams,
                message),
            "MCP_INVALID_PARAMS");

    private static bool TryParseRegister(
        JsonElement arguments,
        out DraftRegistrationCommand? command,
        out string? error)
    {
        command = null;
        if (!TryReadCommonArguments(arguments, out var projectId, out var payload, out error) ||
            !HasOnlyProperties(payload, "artifactId", "expectedVersion", "mediaType", "content") ||
            !TryReadString(payload, "artifactId", 128, allowControls: false, out var artifactId) ||
            !TryReadPositiveInt(payload, "expectedVersion", out var version) ||
            !TryReadString(payload, "mediaType", 64, allowControls: false, out var mediaType) ||
            !TryReadString(payload, "content", DraftAuthoringService.MaximumRegistrationBytes, allowControls: true, out var content))
        {
            error = "draft.register payload requires artifactId, expectedVersion, mediaType and bounded content.";
            return false;
        }

        command = new DraftRegistrationCommand(projectId!, artifactId!, version, mediaType!, content!);
        error = null;
        return true;
    }

    private static bool TryParseValidate(
        JsonElement arguments,
        out DraftValidationQuery? query,
        out string? error)
    {
        query = null;
        if (!TryReadCommonArguments(arguments, out var projectId, out var payload, out error) ||
            !HasOnlyProperties(payload, "artifactId", "version", "maximumLineLength") ||
            !TryReadString(payload, "artifactId", 128, allowControls: false, out var artifactId) ||
            !TryReadPositiveInt(payload, "version", out var version))
        {
            error = "draft.validate payload requires artifactId, positive version and optional maximumLineLength.";
            return false;
        }

        var maximumLineLength = 120;
        if (payload.TryGetProperty("maximumLineLength", out var maximum))
        {
            if (maximum.ValueKind != JsonValueKind.Number ||
                !maximum.TryGetInt32(out maximumLineLength) ||
                maximumLineLength is < 40 or > 240)
            {
                error = "maximumLineLength must be an integer between 40 and 240.";
                return false;
            }
        }

        query = new DraftValidationQuery(projectId!, artifactId!, version, maximumLineLength);
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
            !TryReadString(arguments, "projectId", 64, allowControls: false, out projectId) ||
            !arguments.TryGetProperty("payload", out payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            error = "Tool arguments require exactly projectId and object payload.";
            return false;
        }
        error = null;
        return true;
    }

    private static bool TryParseDraftResourceUri(
        string uriText,
        out DraftResourceQuery? query)
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

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 5 ||
            !string.Equals(segments[1], "artifact", StringComparison.Ordinal) ||
            !string.Equals(segments[3], "versions", StringComparison.Ordinal) ||
            !int.TryParse(
                segments[4],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var version) ||
            version < 1)
        {
            return false;
        }

        query = new DraftResourceQuery(
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

    private static bool HasOnlyProperties(JsonElement source, params string[] allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        return source.ValueKind == JsonValueKind.Object &&
               source.EnumerateObject().All(property => set.Contains(property.Name));
    }

    private static bool TryReadString(
        JsonElement source,
        string propertyName,
        int maximumLength,
        bool allowControls,
        out string? value)
    {
        value = null;
        if (!source.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString();
        return !string.IsNullOrEmpty(value) &&
               value.Length <= maximumLength &&
               (allowControls || value.All(character => !char.IsControl(character)));
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
            ? "Authoring tool execution failed."
            : sanitized;
    }

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);
}

public sealed record AuthoringCallToolResult(
    [property: JsonPropertyName("content")] IReadOnlyList<object> Content,
    [property: JsonPropertyName("structuredContent")] AuthoringStructuredResult StructuredContent,
    [property: JsonPropertyName("isError")] bool IsError);

public sealed record AuthoringStructuredResult(
    [property: JsonPropertyName("resultType")] string ResultType,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("artifactRefs")] IReadOnlyList<DraftArtifactReference> ArtifactRefs,
    [property: JsonPropertyName("warnings")] IReadOnlyList<DraftWarning> Warnings,
    [property: JsonPropertyName("data")] object Data,
    [property: JsonPropertyName("error")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    AuthoringError? Error);

public sealed record AuthoringError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

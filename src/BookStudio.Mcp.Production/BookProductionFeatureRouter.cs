using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookStudio.Application.Production;
using BookStudio.Mcp.BookCore;
using BookStudio.Mcp.Protocol;

namespace BookStudio.Mcp.Production;

/// <summary>Executable release preparation, preflight and production schema/profile resources.</summary>
public sealed class BookProductionFeatureRouter : IMcpFeatureRouter, IAsyncDisposable
{
    private const int ToolPageSize = 50;
    private const int ResourcePageSize = 4;

    private static readonly IReadOnlyDictionary<string, object> ServerCapabilities =
        new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["tools"] = new Dictionary<string, object>(StringComparer.Ordinal) { ["listChanged"] = false },
            ["resources"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["subscribe"] = false,
                ["listChanged"] = false,
            },
        };

    private readonly Lazy<IReleaseProductionService> _service;
    private readonly Func<ValueTask>? _disposeAsync;
    private int _disposed;

    public BookProductionFeatureRouter(
        Func<IReleaseProductionService> serviceFactory,
        Func<ValueTask>? disposeAsync = null)
    {
        ArgumentNullException.ThrowIfNull(serviceFactory);
        _service = new Lazy<IReleaseProductionService>(serviceFactory, LazyThreadSafetyMode.ExecutionAndPublication);
        _disposeAsync = disposeAsync;
    }

    public IReadOnlyDictionary<string, object> Capabilities => ServerCapabilities;

    public string Instructions =>
        "BookStudio production can prepare immutable release manifests and run deterministic release-basic preflight. Rendering and publishing tools are not exposed yet.";

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
            "tools/call" => await HandleToolsCallAsync(parameters, requestId, cancellationToken).ConfigureAwait(false),
            "resources/list" => HandleResourcesList(parameters, requestId),
            "resources/read" => HandleResourceRead(parameters, requestId),
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

    private static McpDispatchResult HandleToolsList(JsonElement? parameters, JsonElement requestId)
    {
        if (!TryReadCursor(parameters, "production-tools", BookProductionToolCatalog.ToolCatalogFingerprint, out var offset, out var error))
        {
            return InvalidParams(requestId, error!);
        }
        var page = Slice(BookProductionToolCatalog.ActiveTools, offset, ToolPageSize, "production-tools", BookProductionToolCatalog.ToolCatalogFingerprint);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal) { ["tools"] = page.Items };
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
            !HasOnlyProperties(parameters.Value, "name", "arguments") ||
            !parameters.Value.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String ||
            !parameters.Value.TryGetProperty("arguments", out var arguments) ||
            arguments.ValueKind != JsonValueKind.Object)
        {
            return InvalidParams(requestId, "tools/call params require exactly name and object arguments.");
        }

        var toolName = nameElement.GetString();
        if (string.IsNullOrWhiteSpace(toolName) ||
            toolName.Length > 128 ||
            !BookProductionToolCatalog.ActiveByName.ContainsKey(toolName))
        {
            return InvalidParams(requestId, "Unknown tool.");
        }

        try
        {
            return toolName switch
            {
                BookProductionToolCatalog.ReleasePrepare => await CallPrepareAsync(arguments, requestId, cancellationToken).ConfigureAwait(false),
                BookProductionToolCatalog.PreflightRun => await CallPreflightAsync(arguments, requestId, cancellationToken).ConfigureAwait(false),
                _ => InvalidParams(requestId, "Unknown tool."),
            };
        }
        catch (ReleaseProductionException exception)
        {
            return ToolFailure(requestId, toolName, exception.Code, exception.Message);
        }
    }

    private async ValueTask<McpDispatchResult> CallPrepareAsync(
        JsonElement arguments,
        JsonElement requestId,
        CancellationToken cancellationToken)
    {
        if (!TryParsePrepare(arguments, out var command, out var error))
        {
            return ToolFailure(requestId, BookProductionToolCatalog.ReleasePrepare, "invalid_arguments", error!);
        }

        var result = await _service.Value.PrepareAsync(command!, cancellationToken).ConfigureAwait(false);
        var structured = new ProductionStructuredResult(
            "complete",
            ReleaseProductionService.BuildOperationId(
                BookProductionToolCatalog.ReleasePrepare,
                result.Release.ArtifactId,
                result.Release.Version.ToString(CultureInfo.InvariantCulture),
                result.Release.Sha256),
            [result.Release],
            [],
            new { release = result.Release, manifest = result.Manifest },
            Error: null);
        return ToolSuccess(
            requestId,
            $"Release {result.Manifest.ReleaseId} version {result.Release.Version} was prepared immutably.",
            structured);
    }

    private async ValueTask<McpDispatchResult> CallPreflightAsync(
        JsonElement arguments,
        JsonElement requestId,
        CancellationToken cancellationToken)
    {
        if (!TryParsePreflight(arguments, out var query, out var error))
        {
            return ToolFailure(requestId, BookProductionToolCatalog.PreflightRun, "invalid_arguments", error!);
        }

        var result = await _service.Value.RunPreflightAsync(query!, cancellationToken).ConfigureAwait(false);
        var structured = new ProductionStructuredResult(
            "complete",
            ReleaseProductionService.BuildOperationId(
                BookProductionToolCatalog.PreflightRun,
                result.Release.ArtifactId,
                result.Release.Version.ToString(CultureInfo.InvariantCulture),
                result.Release.Sha256,
                result.Profile),
            [result.Release],
            [],
            new
            {
                profile = result.Profile,
                decision = result.Decision,
                checks = result.Checks,
                blockingReasons = result.BlockingReasons,
                release = result.Release,
            },
            Error: null);
        return ToolSuccess(
            requestId,
            $"Release preflight {result.Profile} returned {result.Decision}.",
            structured);
    }

    private static McpDispatchResult HandleResourcesList(JsonElement? parameters, JsonElement requestId)
    {
        if (!TryReadCursor(parameters, "production-resources", BookProductionToolCatalog.ResourceCatalogFingerprint, out var offset, out var error))
        {
            return InvalidParams(requestId, error!);
        }
        var page = Slice(BookProductionToolCatalog.Resources, offset, ResourcePageSize, "production-resources", BookProductionToolCatalog.ResourceCatalogFingerprint);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal) { ["resources"] = page.Items };
        if (page.NextCursor is not null)
        {
            result["nextCursor"] = page.NextCursor;
        }
        return new McpDispatchResult(JsonRpcMessageWriter.Result(requestId, result));
    }

    private static McpDispatchResult HandleResourceRead(JsonElement? parameters, JsonElement requestId)
    {
        if (parameters is null ||
            !HasOnlyProperties(parameters.Value, "uri") ||
            !parameters.Value.TryGetProperty("uri", out var uriElement) ||
            uriElement.ValueKind != JsonValueKind.String)
        {
            return InvalidParams(requestId, "resources/read params require exactly one string uri.");
        }
        var uri = uriElement.GetString();
        if (string.IsNullOrWhiteSpace(uri) ||
            uri.Length > 512 ||
            !BookProductionSchemas.ResourceDocuments.TryGetValue(uri, out var content))
        {
            return InvalidParams(requestId, "Unknown resource URI.");
        }
        var mimeType = uri.StartsWith("book://production/profiles/", StringComparison.Ordinal)
            ? "application/json"
            : "application/schema+json";
        return new McpDispatchResult(
            JsonRpcMessageWriter.Result(
                requestId,
                new
                {
                    contents = new[] { new McpResourceContent(uri, mimeType, content, Blob: null) },
                }));
    }

    private static McpDispatchResult ToolSuccess(
        JsonElement requestId,
        string summary,
        ProductionStructuredResult structured) =>
        new(JsonRpcMessageWriter.Result(
            requestId,
            new ProductionCallToolResult(
                [new McpTextContent("text", summary)],
                structured,
                IsError: false)));

    private static McpDispatchResult ToolFailure(
        JsonElement requestId,
        string toolName,
        string code,
        string safeMessage)
    {
        var bounded = BoundMessage(safeMessage);
        var structured = new ProductionStructuredResult(
            "failed",
            ReleaseProductionService.BuildOperationId(toolName, code),
            [],
            [],
            new Dictionary<string, object>(StringComparer.Ordinal),
            new ProductionError(code, bounded));
        return new McpDispatchResult(
            JsonRpcMessageWriter.Result(
                requestId,
                new ProductionCallToolResult(
                    [new McpTextContent("text", bounded)],
                    structured,
                    IsError: true)),
            "MCP_PRODUCTION_TOOL_FAILED");
    }

    private static McpDispatchResult InvalidParams(JsonElement requestId, string message) =>
        new(
            JsonRpcMessageWriter.Error(requestId, JsonRpcErrorCodes.InvalidParams, message),
            "MCP_INVALID_PARAMS");

    private static bool TryParsePrepare(
        JsonElement arguments,
        out ReleasePreparationCommand? command,
        out string? error)
    {
        command = null;
        if (!TryReadCommon(arguments, out var projectId, out var payload, out error) ||
            !HasOnlyProperties(payload, "releaseId", "expectedVersion", "title", "language", "sources") ||
            !TryReadString(payload, "releaseId", 64, out var releaseId) ||
            !TryReadPositiveInt(payload, "expectedVersion", out var expectedVersion) ||
            !TryReadString(payload, "title", 200, out var title) ||
            !TryReadString(payload, "language", 32, out var language) ||
            !payload.TryGetProperty("sources", out var sourcesElement) ||
            sourcesElement.ValueKind != JsonValueKind.Array ||
            sourcesElement.GetArrayLength() is < 1 or > 50)
        {
            error = "release.prepare payload requires releaseId, expectedVersion, title, language and 1..50 sources.";
            return false;
        }

        var sources = new List<ReleaseSourceRequest>(sourcesElement.GetArrayLength());
        foreach (var source in sourcesElement.EnumerateArray())
        {
            if (!HasOnlyProperties(source, "role", "artifactId", "version") ||
                !TryReadString(source, "role", 32, out var role) ||
                !TryReadString(source, "artifactId", 128, out var artifactId) ||
                !TryReadPositiveInt(source, "version", out var version))
            {
                error = "Every source requires role, artifactId and positive version.";
                return false;
            }
            sources.Add(new ReleaseSourceRequest(role!, artifactId!, version));
        }

        command = new ReleasePreparationCommand(
            projectId!, releaseId!, expectedVersion, title!, language!, sources);
        error = null;
        return true;
    }

    private static bool TryParsePreflight(
        JsonElement arguments,
        out ReleasePreflightQuery? query,
        out string? error)
    {
        query = null;
        if (!TryReadCommon(arguments, out var projectId, out var payload, out error) ||
            !HasOnlyProperties(payload, "releaseArtifactId", "version", "profile") ||
            !TryReadString(payload, "releaseArtifactId", 128, out var artifactId) ||
            !TryReadPositiveInt(payload, "version", out var version))
        {
            error = "preflight payload requires releaseArtifactId, positive version and optional release-basic profile.";
            return false;
        }

        var profile = "release-basic";
        if (payload.TryGetProperty("profile", out var profileElement))
        {
            if (profileElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(profileElement.GetString()))
            {
                error = "profile must be a non-empty string.";
                return false;
            }
            profile = profileElement.GetString()!;
        }

        query = new ReleasePreflightQuery(projectId!, artifactId!, version, profile);
        error = null;
        return true;
    }

    private static bool TryReadCommon(
        JsonElement arguments,
        out string? projectId,
        out JsonElement payload,
        out string? error)
    {
        projectId = null;
        payload = default;
        if (!HasOnlyProperties(arguments, "projectId", "payload") ||
            !TryReadString(arguments, "projectId", 64, out projectId) ||
            !arguments.TryGetProperty("payload", out payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            error = "Tool arguments require exactly projectId and object payload.";
            return false;
        }
        error = null;
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
            offset = McpCursorCodec.Decode(cursorElement.GetString() ?? string.Empty, scope, fingerprint);
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
               value.All(character => !char.IsControl(character));
    }

    private static bool TryReadPositiveInt(JsonElement source, string propertyName, out int value)
    {
        value = 0;
        return source.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value) &&
               value > 0;
    }

    private static string BoundMessage(string message)
    {
        var sanitized = new string(message.Where(character => !char.IsControl(character)).Take(512).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "Production tool execution failed." : sanitized;
    }

    private void EnsureActive() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);
}

public sealed record ProductionCallToolResult(
    [property: JsonPropertyName("content")] IReadOnlyList<object> Content,
    [property: JsonPropertyName("structuredContent")] ProductionStructuredResult StructuredContent,
    [property: JsonPropertyName("isError")] bool IsError);

public sealed record ProductionStructuredResult(
    [property: JsonPropertyName("resultType")] string ResultType,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("artifactRefs")] IReadOnlyList<ReleaseArtifactReference> ArtifactRefs,
    [property: JsonPropertyName("warnings")] IReadOnlyList<object> Warnings,
    [property: JsonPropertyName("data")] object Data,
    [property: JsonPropertyName("error")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ProductionError? Error);

public sealed record ProductionError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

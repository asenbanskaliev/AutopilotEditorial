using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookStudio.Application.Operations;
using BookStudio.Mcp.BookCore;
using BookStudio.Mcp.Protocol;

namespace BookStudio.Mcp.Ops;

/// <summary>Executable read-only operations status, diagnostics and capability resources.</summary>
public sealed class BookOpsFeatureRouter : IMcpFeatureRouter, IAsyncDisposable
{
    private const int ToolPageSize = 50;
    private const int ResourcePageSize = 3;

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

    private readonly Lazy<IOperationsDiagnosticsService> _service;
    private readonly Func<ValueTask>? _disposeAsync;
    private int _disposed;

    public BookOpsFeatureRouter(
        Func<IOperationsDiagnosticsService> serviceFactory,
        Func<ValueTask>? disposeAsync = null)
    {
        ArgumentNullException.ThrowIfNull(serviceFactory);
        _service = new Lazy<IOperationsDiagnosticsService>(
            serviceFactory,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _disposeAsync = disposeAsync;
    }

    public IReadOnlyDictionary<string, object> Capabilities => ServerCapabilities;

    public string Instructions =>
        "BookStudio operations can report sanitized readiness and deterministic capability diagnostics. Autopilot workflow controls are reserved until the durable F4 engine exists.";

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
            "tools/call" => await HandleToolsCallAsync(
                    parameters,
                    requestId,
                    cancellationToken)
                .ConfigureAwait(false),
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

    private static McpDispatchResult HandleToolsList(
        JsonElement? parameters,
        JsonElement requestId)
    {
        if (!TryReadCursor(
                parameters,
                "ops-tools",
                BookOpsToolCatalog.ToolCatalogFingerprint,
                out var offset,
                out var error))
        {
            return InvalidParams(requestId, error!);
        }

        var page = Slice(
            BookOpsToolCatalog.ActiveTools,
            offset,
            ToolPageSize,
            "ops-tools",
            BookOpsToolCatalog.ToolCatalogFingerprint);
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
            !HasOnlyProperties(parameters.Value, "name", "arguments") ||
            !parameters.Value.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String ||
            !parameters.Value.TryGetProperty("arguments", out var arguments) ||
            arguments.ValueKind != JsonValueKind.Object ||
            arguments.EnumerateObject().Any())
        {
            return InvalidParams(
                requestId,
                "tools/call params require exactly name and empty object arguments.");
        }

        var toolName = nameElement.GetString();
        if (string.IsNullOrWhiteSpace(toolName) ||
            toolName.Length > 128 ||
            !BookOpsToolCatalog.ActiveByName.ContainsKey(toolName))
        {
            return InvalidParams(requestId, "Unknown tool.");
        }

        try
        {
            return toolName switch
            {
                BookOpsToolCatalog.OpsStatus => await CallStatusAsync(
                        requestId,
                        cancellationToken)
                    .ConfigureAwait(false),
                BookOpsToolCatalog.OpsDiagnostics => await CallDiagnosticsAsync(
                        requestId,
                        cancellationToken)
                    .ConfigureAwait(false),
                _ => InvalidParams(requestId, "Unknown tool."),
            };
        }
        catch (OperationsDiagnosticsException exception)
        {
            return ToolFailure(requestId, toolName, exception.Code, exception.Message);
        }
    }

    private async ValueTask<McpDispatchResult> CallStatusAsync(
        JsonElement requestId,
        CancellationToken cancellationToken)
    {
        var result = await _service.Value.GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        var structured = new OpsStructuredResult(
            "complete",
            StableOperationId(
                BookOpsToolCatalog.OpsStatus,
                result.Status,
                result.ProbeCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                result.ReadyProbeCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.Join(',', result.UnreadyProbes)),
            [],
            [],
            result,
            Error: null);
        return ToolSuccess(
            requestId,
            $"Operations status is {result.Status}; {result.ReadyProbeCount} of {result.ProbeCount} probes are ready.",
            structured);
    }

    private async ValueTask<McpDispatchResult> CallDiagnosticsAsync(
        JsonElement requestId,
        CancellationToken cancellationToken)
    {
        var result = await _service.Value.RunDiagnosticsAsync(cancellationToken)
            .ConfigureAwait(false);
        var structured = new OpsStructuredResult(
            "complete",
            OperationsDiagnosticsService.BuildOperationId(
                BookOpsToolCatalog.OpsDiagnostics,
                result.Status,
                result.Checks),
            [],
            [],
            result,
            Error: null);
        return ToolSuccess(
            requestId,
            $"Operations diagnostics completed with status {result.Status} and {result.Checks.Count} readiness checks.",
            structured);
    }

    private static McpDispatchResult HandleResourcesList(
        JsonElement? parameters,
        JsonElement requestId)
    {
        if (!TryReadCursor(
                parameters,
                "ops-resources",
                BookOpsToolCatalog.ResourceCatalogFingerprint,
                out var offset,
                out var error))
        {
            return InvalidParams(requestId, error!);
        }

        var page = Slice(
            BookOpsToolCatalog.Resources,
            offset,
            ResourcePageSize,
            "ops-resources",
            BookOpsToolCatalog.ResourceCatalogFingerprint);
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

    private static McpDispatchResult HandleResourceRead(
        JsonElement? parameters,
        JsonElement requestId)
    {
        if (parameters is null ||
            !HasOnlyProperties(parameters.Value, "uri") ||
            !parameters.Value.TryGetProperty("uri", out var uriElement) ||
            uriElement.ValueKind != JsonValueKind.String)
        {
            return InvalidParams(
                requestId,
                "resources/read params require exactly one string uri.");
        }

        var uri = uriElement.GetString();
        if (string.IsNullOrWhiteSpace(uri) ||
            uri.Length > 512 ||
            !BookOpsSchemas.ResourceDocuments.TryGetValue(uri, out var content))
        {
            return InvalidParams(requestId, "Unknown resource URI.");
        }

        var mimeType = uri == "book://ops/capabilities"
            ? "application/json"
            : "application/schema+json";
        return new McpDispatchResult(
            JsonRpcMessageWriter.Result(
                requestId,
                new
                {
                    contents = new[]
                    {
                        new McpResourceContent(uri, mimeType, content, Blob: null),
                    },
                }));
    }

    private static McpDispatchResult ToolSuccess(
        JsonElement requestId,
        string summary,
        OpsStructuredResult structured) =>
        new(JsonRpcMessageWriter.Result(
            requestId,
            new OpsCallToolResult(
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
        var structured = new OpsStructuredResult(
            "failed",
            StableOperationId(toolName, code),
            [],
            [],
            new Dictionary<string, object>(StringComparer.Ordinal),
            new OpsError(code, bounded));
        return new McpDispatchResult(
            JsonRpcMessageWriter.Result(
                requestId,
                new OpsCallToolResult(
                    [new McpTextContent("text", bounded)],
                    structured,
                    IsError: true)),
            "MCP_OPS_TOOL_FAILED");
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

    private static string StableOperationId(params string[] parts)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join('|', parts)));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    private static string BoundMessage(string message)
    {
        var sanitized = new string(message
            .Where(character => !char.IsControl(character))
            .Take(512)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized)
            ? "Operations tool execution failed."
            : sanitized;
    }

    private void EnsureActive() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);
}

public sealed record OpsCallToolResult(
    [property: JsonPropertyName("content")] IReadOnlyList<object> Content,
    [property: JsonPropertyName("structuredContent")] OpsStructuredResult StructuredContent,
    [property: JsonPropertyName("isError")] bool IsError);

public sealed record OpsStructuredResult(
    [property: JsonPropertyName("resultType")] string ResultType,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("artifactRefs")] IReadOnlyList<object> ArtifactRefs,
    [property: JsonPropertyName("warnings")] IReadOnlyList<object> Warnings,
    [property: JsonPropertyName("data")] object Data,
    [property: JsonPropertyName("error")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    OpsError? Error);

public sealed record OpsError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

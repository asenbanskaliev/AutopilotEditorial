using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookStudio.Application.Quality;
using BookStudio.Mcp.BookCore;
using BookStudio.Mcp.Protocol;

namespace BookStudio.Mcp.Quality;

/// <summary>Executable deterministic quality tools and profile/schema resources.</summary>
public sealed class BookQualityFeatureRouter : IMcpFeatureRouter, IAsyncDisposable
{
    private const int ToolPageSize = 50;
    private const int ResourcePageSize = 4;
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

    private readonly Lazy<IQualityAssessmentService> _service;
    private readonly Func<ValueTask>? _disposeAsync;
    private int _disposed;

    public BookQualityFeatureRouter(
        Func<IQualityAssessmentService> serviceFactory,
        Func<ValueTask>? disposeAsync = null)
    {
        ArgumentNullException.ThrowIfNull(serviceFactory);
        _service = new Lazy<IQualityAssessmentService>(
            serviceFactory,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _disposeAsync = disposeAsync;
    }

    public IReadOnlyDictionary<string, object> Capabilities => ServerCapabilities;

    public string Instructions =>
        "BookStudio quality is ready to run deterministic read-only draft audits and evaluate the draft-basic gate. Repair and memory tools are not exposed yet.";

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
                "quality-tools",
                BookQualityToolCatalog.ToolCatalogFingerprint,
                out var offset,
                out var error))
        {
            return InvalidParams(requestId, error!);
        }

        var page = Slice(
            BookQualityToolCatalog.ActiveTools,
            offset,
            ToolPageSize,
            "quality-tools",
            BookQualityToolCatalog.ToolCatalogFingerprint);
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
            !BookQualityToolCatalog.ActiveByName.ContainsKey(toolName))
        {
            return InvalidParams(requestId, "Unknown tool.");
        }

        try
        {
            return toolName switch
            {
                BookQualityToolCatalog.AuditRun => await CallAuditAsync(
                        arguments,
                        requestId,
                        cancellationToken)
                    .ConfigureAwait(false),
                BookQualityToolCatalog.GateEvaluate => await CallGateAsync(
                        arguments,
                        requestId,
                        cancellationToken)
                    .ConfigureAwait(false),
                _ => InvalidParams(requestId, "Unknown tool."),
            };
        }
        catch (QualityAssessmentException exception)
        {
            return ToolFailure(requestId, toolName, exception.Code, exception.Message);
        }
    }

    private async ValueTask<McpDispatchResult> CallAuditAsync(
        JsonElement arguments,
        JsonElement requestId,
        CancellationToken cancellationToken)
    {
        if (!TryParseAudit(arguments, out var query, out var error))
        {
            return ToolFailure(
                requestId,
                BookQualityToolCatalog.AuditRun,
                "invalid_arguments",
                error!);
        }

        var result = await _service.Value.RunAuditAsync(query!, cancellationToken)
            .ConfigureAwait(false);
        var warnings = result.Checks
            .Where(check => string.Equals(check.Status, "warn", StringComparison.Ordinal))
            .ToArray();
        var structured = new QualityStructuredResult(
            "complete",
            QualityAssessmentService.BuildOperationId(
                BookQualityToolCatalog.AuditRun,
                result.Artifact.ArtifactId,
                result.Artifact.Version.ToString(CultureInfo.InvariantCulture),
                result.Artifact.Sha256,
                query!.MinimumWords.ToString(CultureInfo.InvariantCulture),
                query.MaximumSentenceWords.ToString(CultureInfo.InvariantCulture)),
            [result.Artifact],
            warnings,
            new
            {
                artifact = result.Artifact,
                metrics = result.Metrics,
                checks = result.Checks,
                isPassing = result.IsPassing,
            },
            Error: null);
        var failedChecks = result.Checks.Count(check =>
            string.Equals(check.Status, "fail", StringComparison.Ordinal));
        return ToolSuccess(
            requestId,
            $"Quality audit completed with {failedChecks} failing checks and {warnings.Length} warnings.",
            structured);
    }

    private async ValueTask<McpDispatchResult> CallGateAsync(
        JsonElement arguments,
        JsonElement requestId,
        CancellationToken cancellationToken)
    {
        if (!TryParseGate(arguments, out var query, out var error))
        {
            return ToolFailure(
                requestId,
                BookQualityToolCatalog.GateEvaluate,
                "invalid_arguments",
                error!);
        }

        var result = await _service.Value.EvaluateGateAsync(query!, cancellationToken)
            .ConfigureAwait(false);
        var warnings = result.Audit.Checks
            .Where(check => string.Equals(check.Status, "warn", StringComparison.Ordinal))
            .ToArray();
        var structured = new QualityStructuredResult(
            "complete",
            QualityAssessmentService.BuildOperationId(
                BookQualityToolCatalog.GateEvaluate,
                result.Audit.Artifact.ArtifactId,
                result.Audit.Artifact.Version.ToString(CultureInfo.InvariantCulture),
                result.Audit.Artifact.Sha256,
                result.Profile,
                query!.MaximumWarnings.ToString(CultureInfo.InvariantCulture),
                query.BlockOnPlaceholders.ToString(CultureInfo.InvariantCulture)),
            [result.Audit.Artifact],
            warnings,
            new
            {
                profile = result.Profile,
                decision = result.Decision,
                blockingReasons = result.BlockingReasons,
                audit = new
                {
                    artifact = result.Audit.Artifact,
                    metrics = result.Audit.Metrics,
                    checks = result.Audit.Checks,
                    isPassing = result.Audit.IsPassing,
                },
            },
            Error: null);
        return ToolSuccess(
            requestId,
            $"Quality gate {result.Profile} returned {result.Decision}.",
            structured);
    }

    private static McpDispatchResult HandleResourcesList(
        JsonElement? parameters,
        JsonElement requestId)
    {
        if (!TryReadCursor(
                parameters,
                "quality-resources",
                BookQualityToolCatalog.ResourceCatalogFingerprint,
                out var offset,
                out var error))
        {
            return InvalidParams(requestId, error!);
        }

        var page = Slice(
            BookQualityToolCatalog.Resources,
            offset,
            ResourcePageSize,
            "quality-resources",
            BookQualityToolCatalog.ResourceCatalogFingerprint);
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
            return InvalidParams(requestId, "resources/read params require exactly one string uri.");
        }

        var uri = uriElement.GetString();
        if (string.IsNullOrWhiteSpace(uri) ||
            uri.Length > 512 ||
            !BookQualitySchemas.ResourceDocuments.TryGetValue(uri, out var content))
        {
            return InvalidParams(requestId, "Unknown resource URI.");
        }

        var mimeType = uri.StartsWith("book://quality/profiles/", StringComparison.Ordinal)
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
        QualityStructuredResult structured) =>
        new(JsonRpcMessageWriter.Result(
            requestId,
            new QualityCallToolResult(
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
        var structured = new QualityStructuredResult(
            "failed",
            QualityAssessmentService.BuildOperationId(toolName, code),
            [],
            [],
            new Dictionary<string, object>(StringComparer.Ordinal),
            new QualityError(code, bounded));
        return new McpDispatchResult(
            JsonRpcMessageWriter.Result(
                requestId,
                new QualityCallToolResult(
                    [new McpTextContent("text", bounded)],
                    structured,
                    IsError: true)),
            "MCP_QUALITY_TOOL_FAILED");
    }

    private static McpDispatchResult InvalidParams(JsonElement requestId, string message) =>
        new(
            JsonRpcMessageWriter.Error(
                requestId,
                JsonRpcErrorCodes.InvalidParams,
                message),
            "MCP_INVALID_PARAMS");

    private static bool TryParseAudit(
        JsonElement arguments,
        out QualityAuditQuery? query,
        out string? error)
    {
        query = null;
        if (!TryReadCommon(arguments, out var projectId, out var payload, out error) ||
            !HasOnlyProperties(payload, "artifactId", "version", "minimumWords", "maximumSentenceWords") ||
            !TryReadString(payload, "artifactId", 128, out var artifactId) ||
            !TryReadPositiveInt(payload, "version", out var version))
        {
            error = "audit payload requires artifactId, positive version and optional bounded thresholds.";
            return false;
        }

        var minimumWords = 1;
        if (payload.TryGetProperty("minimumWords", out var minimum) &&
            (minimum.ValueKind != JsonValueKind.Number ||
             !minimum.TryGetInt32(out minimumWords) ||
             minimumWords is < 1 or > 50_000))
        {
            error = "minimumWords must be an integer between 1 and 50000.";
            return false;
        }

        var maximumSentenceWords = 60;
        if (payload.TryGetProperty("maximumSentenceWords", out var maximum) &&
            (maximum.ValueKind != JsonValueKind.Number ||
             !maximum.TryGetInt32(out maximumSentenceWords) ||
             maximumSentenceWords is < 10 or > 300))
        {
            error = "maximumSentenceWords must be an integer between 10 and 300.";
            return false;
        }

        query = new QualityAuditQuery(
            projectId!,
            artifactId!,
            version,
            minimumWords,
            maximumSentenceWords);
        error = null;
        return true;
    }

    private static bool TryParseGate(
        JsonElement arguments,
        out QualityGateQuery? query,
        out string? error)
    {
        query = null;
        if (!TryReadCommon(arguments, out var projectId, out var payload, out error) ||
            !HasOnlyProperties(
                payload,
                "artifactId",
                "version",
                "profile",
                "minimumWords",
                "maximumWarnings",
                "blockOnPlaceholders") ||
            !TryReadString(payload, "artifactId", 128, out var artifactId) ||
            !TryReadPositiveInt(payload, "version", out var version))
        {
            error = "gate payload requires artifactId, positive version and optional draft-basic profile settings.";
            return false;
        }

        var profile = "draft-basic";
        if (payload.TryGetProperty("profile", out var profileElement) &&
            (profileElement.ValueKind != JsonValueKind.String ||
             string.IsNullOrWhiteSpace(profile = profileElement.GetString())))
        {
            error = "profile must be a non-empty string.";
            return false;
        }

        var minimumWords = 1;
        if (payload.TryGetProperty("minimumWords", out var minimum) &&
            (minimum.ValueKind != JsonValueKind.Number ||
             !minimum.TryGetInt32(out minimumWords) ||
             minimumWords is < 1 or > 50_000))
        {
            error = "minimumWords must be an integer between 1 and 50000.";
            return false;
        }

        var maximumWarnings = 3;
        if (payload.TryGetProperty("maximumWarnings", out var warnings) &&
            (warnings.ValueKind != JsonValueKind.Number ||
             !warnings.TryGetInt32(out maximumWarnings) ||
             maximumWarnings is < 0 or > 100))
        {
            error = "maximumWarnings must be an integer between 0 and 100.";
            return false;
        }

        var blockOnPlaceholders = true;
        if (payload.TryGetProperty("blockOnPlaceholders", out var block) &&
            (block.ValueKind is not JsonValueKind.True and not JsonValueKind.False))
        {
            error = "blockOnPlaceholders must be boolean.";
            return false;
        }
        if (payload.TryGetProperty("blockOnPlaceholders", out block))
        {
            blockOnPlaceholders = block.GetBoolean();
        }

        query = new QualityGateQuery(
            projectId!,
            artifactId!,
            version,
            profile!,
            minimumWords,
            maximumWarnings,
            blockOnPlaceholders);
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
            ? "Quality tool execution failed."
            : sanitized;
    }

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);
}

public sealed record QualityCallToolResult(
    [property: JsonPropertyName("content")] IReadOnlyList<object> Content,
    [property: JsonPropertyName("structuredContent")] QualityStructuredResult StructuredContent,
    [property: JsonPropertyName("isError")] bool IsError);

public sealed record QualityStructuredResult(
    [property: JsonPropertyName("resultType")] string ResultType,
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("artifactRefs")] IReadOnlyList<QualityArtifactReference> ArtifactRefs,
    [property: JsonPropertyName("warnings")] IReadOnlyList<QualityCheck> Warnings,
    [property: JsonPropertyName("data")] object Data,
    [property: JsonPropertyName("error")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    QualityError? Error);

public sealed record QualityError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

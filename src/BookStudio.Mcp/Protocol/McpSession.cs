using System.Reflection;
using System.Text.Json;

namespace BookStudio.Mcp.Protocol;

public enum McpSessionState
{
    Created,
    InitializeResponded,
    Ready,
    Closed,
}

/// <summary>Sequential MCP lifecycle and JSON-RPC request dispatcher for one stdio session.</summary>
public sealed class McpSession
{
    private const int MaximumMethodLength = 128;
    private const int MaximumProtocolVersionLength = 32;
    private const int MaximumImplementationFieldLength = 128;
    private const int MaximumImplementationTitleLength = 256;

    private readonly HashSet<string> _usedRequestIds = new(StringComparer.Ordinal);
    private readonly IMcpFeatureRouter _features;
    private readonly McpImplementationInfo _serverInfo;

    public McpSession(
        IMcpFeatureRouter? features = null,
        McpImplementationInfo? serverInfo = null)
    {
        _features = features ?? EmptyMcpFeatureRouter.Instance;
        _serverInfo = serverInfo ?? new McpImplementationInfo(
            "bookstudio",
            GetServerVersion(),
            "BookStudio MCP");
        ValidateServerInfo(_serverInfo);
    }

    public McpSessionState State { get; private set; } = McpSessionState.Created;

    public string? NegotiatedProtocolVersion { get; private set; }

    public McpImplementationInfo? ClientInfo { get; private set; }

    public async ValueTask<McpDispatchResult> DispatchAsync(
        JsonElement message,
        CancellationToken cancellationToken = default)
    {
        JsonElement? readableId = TryGetReadableId(message);
        try
        {
            return await DispatchCoreAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new McpDispatchResult(
                JsonRpcMessageWriter.Error(
                    readableId,
                    JsonRpcErrorCodes.InternalError,
                    "Internal error"),
                "MCP_INTERNAL_ERROR");
        }
    }

    public void Close()
    {
        State = McpSessionState.Closed;
    }

    private async ValueTask<McpDispatchResult> DispatchCoreAsync(
        JsonElement message,
        CancellationToken cancellationToken)
    {
        if (State == McpSessionState.Closed)
        {
            return new McpDispatchResult(
                JsonRpcMessageWriter.Error(
                    null,
                    JsonRpcErrorCodes.InvalidRequest,
                    "Session is closed."),
                "MCP_SESSION_CLOSED");
        }

        if (message.ValueKind != JsonValueKind.Object)
        {
            return InvalidRequest(null, "A JSON-RPC message must be an object.");
        }

        if (!message.TryGetProperty("jsonrpc", out var jsonRpc) ||
            jsonRpc.ValueKind != JsonValueKind.String ||
            !string.Equals(jsonRpc.GetString(), "2.0", StringComparison.Ordinal))
        {
            return InvalidRequest(TryGetReadableId(message), "jsonrpc must be exactly 2.0.");
        }

        if (!message.TryGetProperty("method", out var methodElement) ||
            methodElement.ValueKind != JsonValueKind.String)
        {
            return InvalidRequest(TryGetReadableId(message), "method is required and must be a string.");
        }

        var method = methodElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(method) ||
            method.Length > MaximumMethodLength ||
            method.Any(char.IsControl))
        {
            return InvalidRequest(TryGetReadableId(message), "method is invalid.");
        }

        var hasId = message.TryGetProperty("id", out _);
        JsonElement requestId = default;
        if (hasId)
        {
            if (!JsonRpcMessageWriter.TryReadRequestId(
                    message,
                    out requestId,
                    out var idValidationError))
            {
                return InvalidRequest(null, idValidationError ?? "Request id is invalid.");
            }

            var identity = NormalizeId(requestId);
            if (!_usedRequestIds.Add(identity))
            {
                return InvalidRequest(requestId, "Request id has already been used in this session.");
            }
        }

        JsonElement? parameters = null;
        if (message.TryGetProperty("params", out var parameterElement))
        {
            if (parameterElement.ValueKind != JsonValueKind.Object)
            {
                return hasId
                    ? InvalidParams(requestId, "params must be an object when present.")
                    : McpDispatchResult.NoResponse("MCP_INVALID_NOTIFICATION_PARAMS");
            }
            parameters = parameterElement;
        }

        if (method == "ping")
        {
            return HandlePing(hasId, requestId);
        }
        if (method == "initialize")
        {
            return HandleInitialize(message, hasId, requestId);
        }
        if (method == "notifications/initialized")
        {
            return HandleInitializedNotification(hasId, requestId);
        }

        return await HandleFeatureOrUnknownMethodAsync(
                method,
                parameters,
                hasId,
                requestId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static McpDispatchResult HandlePing(bool hasId, JsonElement requestId)
    {
        return hasId
            ? new McpDispatchResult(
                JsonRpcMessageWriter.Result(
                    requestId,
                    new Dictionary<string, object>(StringComparer.Ordinal)))
            : McpDispatchResult.NoResponse("MCP_PING_NOTIFICATION_IGNORED");
    }

    private McpDispatchResult HandleInitialize(
        JsonElement message,
        bool hasId,
        JsonElement requestId)
    {
        if (!hasId)
        {
            return McpDispatchResult.NoResponse("MCP_INITIALIZE_NOTIFICATION_REJECTED");
        }

        if (State != McpSessionState.Created)
        {
            return InvalidRequest(requestId, "initialize may be sent only once at session start.");
        }

        if (!message.TryGetProperty("params", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Object)
        {
            return InvalidParams(requestId, "initialize params are required.");
        }

        if (!TryParseInitializeParameters(parameters, out var parsed, out var validationError))
        {
            return InvalidParams(requestId, validationError ?? "initialize params are invalid.");
        }

        var negotiatedVersion = McpProtocolVersions.Negotiate(parsed!.ProtocolVersion);
        NegotiatedProtocolVersion = negotiatedVersion;
        ClientInfo = parsed.ClientInfo;
        State = McpSessionState.InitializeResponded;

        var instructions = _features.Instructions;
        if (string.IsNullOrWhiteSpace(instructions) ||
            instructions.Length > 1024 ||
            instructions.Any(char.IsControl))
        {
            throw new InvalidOperationException("MCP feature instructions are invalid.");
        }

        var result = new McpInitializeResult(
            negotiatedVersion,
            _features.Capabilities,
            _serverInfo,
            instructions);

        return new McpDispatchResult(JsonRpcMessageWriter.Result(requestId, result));
    }

    private McpDispatchResult HandleInitializedNotification(
        bool hasId,
        JsonElement requestId)
    {
        if (hasId)
        {
            return InvalidRequest(
                requestId,
                "notifications/initialized must be a notification without id.");
        }

        if (State == McpSessionState.InitializeResponded)
        {
            State = McpSessionState.Ready;
            return McpDispatchResult.NoResponse();
        }

        return McpDispatchResult.NoResponse(
            State == McpSessionState.Ready
                ? "MCP_DUPLICATE_INITIALIZED_NOTIFICATION"
                : "MCP_INITIALIZED_BEFORE_INITIALIZE");
    }

    private async ValueTask<McpDispatchResult> HandleFeatureOrUnknownMethodAsync(
        string method,
        JsonElement? parameters,
        bool hasId,
        JsonElement requestId,
        CancellationToken cancellationToken)
    {
        if (!hasId)
        {
            return McpDispatchResult.NoResponse("MCP_UNKNOWN_NOTIFICATION_" + SafeMethodCode(method));
        }

        if (State != McpSessionState.Ready)
        {
            return new McpDispatchResult(
                JsonRpcMessageWriter.Error(
                    requestId,
                    JsonRpcErrorCodes.ServerNotInitialized,
                    "Server not initialized"),
                "MCP_SERVER_NOT_INITIALIZED");
        }

        var featureResult = await _features.TryDispatchAsync(
                method,
                parameters,
                requestId,
                cancellationToken)
            .ConfigureAwait(false);
        if (featureResult is not null)
        {
            return featureResult;
        }

        return new McpDispatchResult(
            JsonRpcMessageWriter.Error(
                requestId,
                JsonRpcErrorCodes.MethodNotFound,
                "Method not found"),
            "MCP_METHOD_NOT_FOUND_" + SafeMethodCode(method));
    }

    private static bool TryParseInitializeParameters(
        JsonElement parameters,
        out McpInitializeParameters? result,
        out string? validationError)
    {
        result = null;

        if (!parameters.TryGetProperty("protocolVersion", out var protocolVersionElement) ||
            protocolVersionElement.ValueKind != JsonValueKind.String)
        {
            validationError = "protocolVersion is required and must be a string.";
            return false;
        }

        var protocolVersion = protocolVersionElement.GetString() ?? string.Empty;
        if (!IsBoundedText(protocolVersion, MaximumProtocolVersionLength))
        {
            validationError = "protocolVersion must contain 1 to 32 safe characters.";
            return false;
        }

        if (!parameters.TryGetProperty("capabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Object)
        {
            validationError = "capabilities is required and must be an object.";
            return false;
        }

        if (!parameters.TryGetProperty("clientInfo", out var clientInfo) ||
            clientInfo.ValueKind != JsonValueKind.Object)
        {
            validationError = "clientInfo is required and must be an object.";
            return false;
        }

        if (!TryReadBoundedProperty(
                clientInfo,
                "name",
                MaximumImplementationFieldLength,
                required: true,
                out var name) ||
            !TryReadBoundedProperty(
                clientInfo,
                "version",
                MaximumImplementationFieldLength,
                required: true,
                out var version) ||
            !TryReadBoundedProperty(
                clientInfo,
                "title",
                MaximumImplementationTitleLength,
                required: false,
                out var title))
        {
            validationError = "clientInfo name, version or title is invalid.";
            return false;
        }

        result = new McpInitializeParameters(
            protocolVersion,
            new McpImplementationInfo(name!, version!, title));
        validationError = null;
        return true;
    }

    private static bool TryReadBoundedProperty(
        JsonElement source,
        string propertyName,
        int maximumLength,
        bool required,
        out string? value)
    {
        value = null;
        if (!source.TryGetProperty(propertyName, out var property))
        {
            return !required;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null && IsBoundedText(value, maximumLength);
    }

    private static void ValidateServerInfo(McpImplementationInfo serverInfo)
    {
        if (!IsBoundedText(serverInfo.Name, MaximumImplementationFieldLength) ||
            !IsBoundedText(serverInfo.Version, MaximumImplementationFieldLength) ||
            serverInfo.Title is not null &&
            !IsBoundedText(serverInfo.Title, MaximumImplementationTitleLength))
        {
            throw new ArgumentException("MCP server identity is invalid.", nameof(serverInfo));
        }
    }

    private static bool IsBoundedText(string value, int maximumLength)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Length <= maximumLength &&
               value.All(character => !char.IsControl(character));
    }

    private static McpDispatchResult InvalidRequest(JsonElement? id, string message)
    {
        return new McpDispatchResult(
            JsonRpcMessageWriter.Error(
                id,
                JsonRpcErrorCodes.InvalidRequest,
                message),
            "MCP_INVALID_REQUEST");
    }

    private static McpDispatchResult InvalidParams(JsonElement id, string message)
    {
        return new McpDispatchResult(
            JsonRpcMessageWriter.Error(
                id,
                JsonRpcErrorCodes.InvalidParams,
                message),
            "MCP_INVALID_PARAMS");
    }

    private static JsonElement? TryGetReadableId(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("id", out var candidate))
        {
            return null;
        }

        if (candidate.ValueKind == JsonValueKind.String)
        {
            var text = candidate.GetString();
            return !string.IsNullOrEmpty(text) && text.Length <= 128
                ? candidate.Clone()
                : null;
        }

        return candidate.ValueKind == JsonValueKind.Number && candidate.TryGetInt64(out _)
            ? candidate.Clone()
            : null;
    }

    private static string NormalizeId(JsonElement id)
    {
        return id.ValueKind == JsonValueKind.String
            ? "s:" + id.GetString()
            : "n:" + id.GetRawText();
    }

    private static string SafeMethodCode(string method)
    {
        var safe = new string(method
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            .Take(48)
            .ToArray());
        return string.IsNullOrEmpty(safe) ? "UNKNOWN" : safe.ToUpperInvariant();
    }

    private static string GetServerVersion()
    {
        var assembly = typeof(McpSession).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
                   ?.Split('+', 2)[0]
               ?? assembly.GetName().Version?.ToString()
               ?? "0.0.0";
    }
}

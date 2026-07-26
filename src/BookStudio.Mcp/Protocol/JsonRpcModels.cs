using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookStudio.Mcp.Protocol;

public static class JsonRpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int ServerNotInitialized = -32002;
}

public sealed record JsonRpcError(
    int Code,
    string Message,
    object? Data = null);

public sealed record McpDispatchResult(
    string? Response,
    string? DiagnosticCode = null)
{
    public static McpDispatchResult NoResponse(string? diagnosticCode = null) =>
        new(null, diagnosticCode);
}

public static class JsonRpcMessageWriter
{
    private static readonly JsonElement NullId = CreateNullId();

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        MaxDepth = 32,
    };

    public static string Result(JsonElement id, object result)
    {
        return JsonSerializer.Serialize(
            new JsonRpcResultEnvelope("2.0", id.Clone(), result),
            Options);
    }

    public static string Error(
        JsonElement? id,
        int code,
        string message,
        object? data = null)
    {
        return JsonSerializer.Serialize(
            new JsonRpcErrorEnvelope(
                "2.0",
                id?.Clone() ?? NullId,
                new JsonRpcError(code, message, data)),
            Options);
    }

    public static bool TryReadRequestId(
        JsonElement message,
        out JsonElement id,
        out string? validationError)
    {
        if (!message.TryGetProperty("id", out var candidate))
        {
            id = default;
            validationError = "Request id is required.";
            return false;
        }

        if (candidate.ValueKind == JsonValueKind.String)
        {
            var text = candidate.GetString();
            if (string.IsNullOrEmpty(text) || text.Length > 128)
            {
                id = default;
                validationError = "String request id must contain 1 to 128 characters.";
                return false;
            }

            id = candidate.Clone();
            validationError = null;
            return true;
        }

        if (candidate.ValueKind == JsonValueKind.Number && candidate.TryGetInt64(out _))
        {
            id = candidate.Clone();
            validationError = null;
            return true;
        }

        id = default;
        validationError = "Request id must be a non-null string or integer.";
        return false;
    }

    private static JsonElement CreateNullId()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }

    private sealed record JsonRpcResultEnvelope(
        [property: JsonPropertyName("jsonrpc")] string JsonRpc,
        [property: JsonPropertyName("id")] JsonElement Id,
        [property: JsonPropertyName("result")] object Result);

    private sealed record JsonRpcErrorEnvelope(
        [property: JsonPropertyName("jsonrpc")] string JsonRpc,
        [property: JsonPropertyName("id")] JsonElement Id,
        [property: JsonPropertyName("error")] JsonRpcError Error);
}

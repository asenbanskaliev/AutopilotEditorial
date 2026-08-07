using System.Text.Json;
using BookStudio.Mcp.BookCore;
using BookStudio.Mcp.Protocol;

namespace BookStudio.Mcp.Prompts;

/// <summary>Strict MCP prompts/list and prompts/get dispatcher for one immutable catalog.</summary>
public static class McpPromptDispatcher
{
    private const int PromptPageSize = 20;
    private const int MaximumPromptNameLength = 128;
    private const int MaximumArgumentNameLength = 64;
    private const int MaximumArgumentValueLength = 256;
    private const int MaximumArgumentCount = 16;

    public static McpDispatchResult? TryDispatch(
        string method,
        JsonElement? parameters,
        JsonElement requestId,
        VersionedMcpPromptCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return method switch
        {
            "prompts/list" => HandleList(parameters, requestId, catalog),
            "prompts/get" => HandleGet(parameters, requestId, catalog),
            _ => null,
        };
    }

    private static McpDispatchResult HandleList(
        JsonElement? parameters,
        JsonElement requestId,
        VersionedMcpPromptCatalog catalog)
    {
        if (!TryReadCursor(parameters, catalog, out var offset, out var error))
        {
            return InvalidParams(requestId, error!);
        }
        if (offset > catalog.Definitions.Count)
        {
            return InvalidParams(requestId, "Cursor offset exceeds the prompt catalog.");
        }

        var prompts = catalog.Definitions
            .Skip(offset)
            .Take(PromptPageSize)
            .ToArray();
        var nextOffset = offset + prompts.Length;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["prompts"] = prompts,
        };
        if (nextOffset < catalog.Definitions.Count)
        {
            result["nextCursor"] = McpCursorCodec.Encode(
                catalog.CursorScope,
                nextOffset,
                catalog.Fingerprint);
        }
        return new McpDispatchResult(
            JsonRpcMessageWriter.Result(requestId, result));
    }

    private static McpDispatchResult HandleGet(
        JsonElement? parameters,
        JsonElement requestId,
        VersionedMcpPromptCatalog catalog)
    {
        if (parameters is null ||
            parameters.Value.ValueKind != JsonValueKind.Object ||
            !parameters.Value.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            return InvalidParams(
                requestId,
                "prompts/get params require a string name and optional string arguments.");
        }

        var name = nameElement.GetString();
        if (string.IsNullOrWhiteSpace(name) ||
            name.Length > MaximumPromptNameLength ||
            name.Any(char.IsControl) ||
            !catalog.TryGetPrompt(name, out var prompt))
        {
            return InvalidParams(requestId, "Unknown prompt.");
        }

        if (!TryReadArguments(parameters.Value, out var arguments, out var error))
        {
            return InvalidParams(requestId, error!);
        }

        try
        {
            var result = prompt.Render(arguments);
            return new McpDispatchResult(
                JsonRpcMessageWriter.Result(requestId, result));
        }
        catch (McpPromptArgumentException exception)
        {
            return InvalidParams(requestId, BoundMessage(exception.Message));
        }
    }

    private static bool TryReadArguments(
        JsonElement parameters,
        out IReadOnlyDictionary<string, string> arguments,
        out string? error)
    {
        arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        error = null;
        if (!parameters.TryGetProperty("arguments", out var argumentElement))
        {
            return true;
        }
        if (argumentElement.ValueKind != JsonValueKind.Object ||
            argumentElement.EnumerateObject().Count() > MaximumArgumentCount)
        {
            error = "Prompt arguments must be a bounded object of strings.";
            return false;
        }

        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in argumentElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) ||
                property.Name.Length > MaximumArgumentNameLength ||
                !property.Name.All(character =>
                    char.IsAsciiLetterOrDigit(character) || character is '_' or '-') ||
                property.Value.ValueKind != JsonValueKind.String)
            {
                error = "Prompt argument names and values are invalid.";
                return false;
            }

            var value = property.Value.GetString();
            if (value is null ||
                value.Length > MaximumArgumentValueLength ||
                value.Any(char.IsControl) ||
                !parsed.TryAdd(property.Name, value))
            {
                error = "Prompt argument values are invalid.";
                return false;
            }
        }
        arguments = parsed;
        return true;
    }

    private static bool TryReadCursor(
        JsonElement? parameters,
        VersionedMcpPromptCatalog catalog,
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
            error = "prompts/list params may contain only cursor.";
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
                catalog.CursorScope,
                catalog.Fingerprint);
            return true;
        }
        catch (McpCursorException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool HasOnlyProperties(
        JsonElement source,
        params string[] allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        return source.ValueKind == JsonValueKind.Object &&
               source.EnumerateObject().All(property => set.Contains(property.Name));
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

    private static string BoundMessage(string message)
    {
        var bounded = new string(message
            .Where(character => !char.IsControl(character))
            .Take(256)
            .ToArray());
        return string.IsNullOrWhiteSpace(bounded)
            ? "Prompt arguments are invalid."
            : bounded;
    }
}

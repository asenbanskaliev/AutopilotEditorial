using System.Text.Json.Serialization;

namespace BookStudio.Mcp.Prompts;

public sealed record McpPromptArgumentDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("required")] bool Required);

public sealed record McpPromptDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("arguments")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<McpPromptArgumentDefinition>? Arguments);

public sealed record McpPromptMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] object Content);

public sealed record McpGetPromptResult(
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("messages")] IReadOnlyList<McpPromptMessage> Messages);

public sealed class McpPromptArgumentException : Exception
{
    public McpPromptArgumentException(string safeMessage)
        : base(safeMessage)
    {
    }
}

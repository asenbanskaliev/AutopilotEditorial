using System.Text.Json.Serialization;

namespace BookStudio.Mcp.Protocol;

public sealed record McpImplementationInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("title")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Title = null);

public sealed record McpInitializeResult(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("capabilities")] IReadOnlyDictionary<string, object> Capabilities,
    [property: JsonPropertyName("serverInfo")] McpImplementationInfo ServerInfo,
    [property: JsonPropertyName("instructions")] string Instructions);

public sealed record McpInitializeParameters(
    string ProtocolVersion,
    McpImplementationInfo ClientInfo);

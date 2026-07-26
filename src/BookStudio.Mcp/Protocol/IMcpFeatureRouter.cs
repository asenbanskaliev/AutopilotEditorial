using System.Text.Json;

namespace BookStudio.Mcp.Protocol;

/// <summary>Routes ready-state MCP feature requests without owning lifecycle or transport.</summary>
public interface IMcpFeatureRouter
{
    IReadOnlyDictionary<string, object> Capabilities { get; }

    string Instructions { get; }

    ValueTask<McpDispatchResult?> TryDispatchAsync(
        string method,
        JsonElement? parameters,
        JsonElement requestId,
        CancellationToken cancellationToken = default);
}

public sealed class EmptyMcpFeatureRouter : IMcpFeatureRouter
{
    public static EmptyMcpFeatureRouter Instance { get; } = new();

    private EmptyMcpFeatureRouter() { }

    public IReadOnlyDictionary<string, object> Capabilities { get; } =
        new Dictionary<string, object>(StringComparer.Ordinal);

    public string Instructions =>
        "BookStudio MCP lifecycle is initialized. No tools, resources or prompts are exposed in this foundation slice.";

    public ValueTask<McpDispatchResult?> TryDispatchAsync(
        string method,
        JsonElement? parameters,
        JsonElement requestId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<McpDispatchResult?>(null);
}

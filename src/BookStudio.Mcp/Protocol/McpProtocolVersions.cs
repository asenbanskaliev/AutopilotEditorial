namespace BookStudio.Mcp.Protocol;

/// <summary>Stable MCP protocol revisions supported by the BookStudio stdio server.</summary>
public static class McpProtocolVersions
{
    public const string Latest = "2025-11-25";

    public static readonly IReadOnlyList<string> Supported =
    [
        Latest,
        "2025-06-18",
        "2025-03-26",
        "2024-11-05",
    ];

    public static string Negotiate(string requestedVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedVersion);
        return Supported.Contains(requestedVersion, StringComparer.Ordinal)
            ? requestedVersion
            : Latest;
    }
}

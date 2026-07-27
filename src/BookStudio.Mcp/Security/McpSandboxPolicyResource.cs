using System.Text.Json;
using BookStudio.Mcp.BookCore;

namespace BookStudio.Mcp.Security;

/// <summary>Path-free public description of the effective MCP sandbox limits.</summary>
public sealed class McpSandboxPolicyResource
{
    public const string Uri = "book://security/sandbox-policy";
    public const string MediaType = "application/vnd.bookstudio.sandbox-policy+json";

    public McpSandboxPolicyResource(McpHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Text = JsonSerializer.Serialize(
            new
            {
                schemaVersion = "1.0.0",
                mode = "strict-local",
                maximumArtifactBytes = options.MaximumArtifactBytes,
                maximumStoreBytes = options.MaximumStoreBytes,
                maximumStoreFiles = options.MaximumStoreFiles,
                workspaceRules = new[]
                {
                    "not-filesystem-root",
                    "local-path",
                    "no-existing-links",
                    "no-existing-file",
                },
                storeRules = new[]
                {
                    "confined-artifacts",
                    "no-links",
                    "immutable-versions",
                    "quota-before-publish",
                },
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Definition = new McpResourceDefinition(
            Uri,
            "sandbox-policy",
            "Effective MCP sandbox policy",
            "Path-free effective limits and filesystem rules enforced by this BookStudio MCP process.",
            MediaType);
    }

    public McpResourceDefinition Definition { get; }

    public string Text { get; }
}

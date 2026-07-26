namespace BookStudio.Mcp;

/// <summary>Safe process-level configuration for the local MCP stdio host.</summary>
public sealed record McpHostOptions(string WorkspaceRoot)
{
    private const string WorkspaceEnvironmentVariable = "BOOKSTUDIO_WORKSPACE_ROOT";

    public static McpHostOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? workspaceRoot = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.StartsWith("--workspace-root=", StringComparison.Ordinal))
            {
                EnsureUnset(workspaceRoot);
                workspaceRoot = argument["--workspace-root=".Length..];
                continue;
            }

            if (string.Equals(argument, "--workspace-root", StringComparison.Ordinal))
            {
                EnsureUnset(workspaceRoot);
                if (++index >= args.Length)
                {
                    throw new ArgumentException("--workspace-root requires a value.", nameof(args));
                }
                workspaceRoot = args[index];
                continue;
            }

            throw new ArgumentException("Unknown MCP host option.", nameof(args));
        }

        workspaceRoot ??= Environment.GetEnvironmentVariable(WorkspaceEnvironmentVariable);
        workspaceRoot ??= DefaultWorkspaceRoot();
        if (string.IsNullOrWhiteSpace(workspaceRoot) ||
            workspaceRoot.Length > 4096 ||
            workspaceRoot.Any(char.IsControl))
        {
            throw new ArgumentException("Workspace root is invalid.", nameof(args));
        }

        return new McpHostOptions(Path.GetFullPath(workspaceRoot));
    }

    private static void EnsureUnset(string? value)
    {
        if (value is not null)
        {
            throw new ArgumentException("Workspace root may be configured only once.");
        }
    }

    private static string DefaultWorkspaceRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            return Path.Combine(local, "BookStudio", "workspace");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(
            string.IsNullOrWhiteSpace(home) ? Directory.GetCurrentDirectory() : home,
            ".bookstudio",
            "workspace");
    }
}

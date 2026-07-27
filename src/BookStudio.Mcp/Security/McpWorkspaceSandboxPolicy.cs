namespace BookStudio.Mcp.Security;

/// <summary>Fail-closed admission policy for a local MCP workspace root.</summary>
public static class McpWorkspaceSandboxPolicy
{
    public static string ValidateAndCanonicalize(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) ||
            workspaceRoot.Length > 4096 ||
            workspaceRoot.Any(char.IsControl))
        {
            throw new ArgumentException("Workspace root is invalid.", nameof(workspaceRoot));
        }

        string canonical;
        try
        {
            canonical = Path.GetFullPath(workspaceRoot);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Workspace root is invalid.", nameof(workspaceRoot));
        }

        var filesystemRoot = Path.GetPathRoot(canonical);
        if (string.IsNullOrWhiteSpace(filesystemRoot) ||
            string.Equals(
                TrimEndingSeparators(canonical),
                TrimEndingSeparators(Path.GetFullPath(filesystemRoot)),
                PathComparison))
        {
            throw new ArgumentException("Workspace root cannot be a filesystem root.", nameof(workspaceRoot));
        }

        if (OperatingSystem.IsWindows() &&
            (canonical.StartsWith("\\\\", StringComparison.Ordinal) ||
             canonical.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
             canonical.StartsWith("\\\\.\\", StringComparison.Ordinal)))
        {
            throw new ArgumentException("Workspace root must be a local non-device path.", nameof(workspaceRoot));
        }

        if (File.Exists(canonical))
        {
            throw new ArgumentException("Workspace root cannot be an existing file.", nameof(workspaceRoot));
        }

        EnsureExistingChainHasNoLinks(canonical);
        return canonical;
    }

    private static void EnsureExistingChainHasNoLinks(string canonicalPath)
    {
        var current = canonicalPath;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                RejectLinkOrReparsePoint(current);
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, PathComparison))
            {
                break;
            }
            current = parent;
        }
    }

    private static void RejectLinkOrReparsePoint(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("Workspace path contains a link or reparse point.", nameof(path));
            }

            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            if (info.LinkTarget is not null)
            {
                throw new ArgumentException("Workspace path contains a symbolic link.", nameof(path));
            }
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ArgumentException("Workspace path cannot be safely inspected.", nameof(path));
        }
    }

    private static string TrimEndingSeparators(string path) =>
        Path.TrimEndingDirectorySeparator(path);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}

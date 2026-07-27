using System.Globalization;
using BookStudio.Mcp.Security;

namespace BookStudio.Mcp;

/// <summary>Safe process-level configuration for the local MCP stdio host.</summary>
public sealed record McpHostOptions(
    string WorkspaceRoot,
    long MaximumArtifactBytes,
    long MaximumStoreBytes,
    int MaximumStoreFiles)
{
    public const long DefaultMaximumArtifactBytes = 16L * 1024L * 1024L;
    public const long DefaultMaximumStoreBytes = 1024L * 1024L * 1024L;
    public const int DefaultMaximumStoreFiles = 100000;

    private const long MinimumArtifactBytes = 1024L;
    private const long MaximumArtifactBytesBound = 256L * 1024L * 1024L;
    private const long MinimumStoreBytes = 64L * 1024L;
    private const long MaximumStoreBytesBound = 16L * 1024L * 1024L * 1024L;
    private const int MinimumStoreFiles = 16;
    private const int MaximumStoreFilesBound = 1000000;
    private const string WorkspaceEnvironmentVariable = "BOOKSTUDIO_WORKSPACE_ROOT";

    public static McpHostOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? workspaceRoot = null;
        string? maximumArtifactBytes = null;
        string? maximumStoreBytes = null;
        string? maximumStoreFiles = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (TryReadEquals(argument, "--workspace-root", out var workspaceEquals))
            {
                EnsureUnset(workspaceRoot, "Workspace root");
                workspaceRoot = workspaceEquals;
                continue;
            }
            if (string.Equals(argument, "--workspace-root", StringComparison.Ordinal))
            {
                EnsureUnset(workspaceRoot, "Workspace root");
                workspaceRoot = ReadNext(args, ref index, "--workspace-root");
                continue;
            }

            if (TryReadEquals(argument, "--max-artifact-bytes", out var artifactEquals))
            {
                EnsureUnset(maximumArtifactBytes, "Maximum artifact bytes");
                maximumArtifactBytes = artifactEquals;
                continue;
            }
            if (string.Equals(argument, "--max-artifact-bytes", StringComparison.Ordinal))
            {
                EnsureUnset(maximumArtifactBytes, "Maximum artifact bytes");
                maximumArtifactBytes = ReadNext(args, ref index, "--max-artifact-bytes");
                continue;
            }

            if (TryReadEquals(argument, "--max-store-bytes", out var storeBytesEquals))
            {
                EnsureUnset(maximumStoreBytes, "Maximum store bytes");
                maximumStoreBytes = storeBytesEquals;
                continue;
            }
            if (string.Equals(argument, "--max-store-bytes", StringComparison.Ordinal))
            {
                EnsureUnset(maximumStoreBytes, "Maximum store bytes");
                maximumStoreBytes = ReadNext(args, ref index, "--max-store-bytes");
                continue;
            }

            if (TryReadEquals(argument, "--max-store-files", out var storeFilesEquals))
            {
                EnsureUnset(maximumStoreFiles, "Maximum store files");
                maximumStoreFiles = storeFilesEquals;
                continue;
            }
            if (string.Equals(argument, "--max-store-files", StringComparison.Ordinal))
            {
                EnsureUnset(maximumStoreFiles, "Maximum store files");
                maximumStoreFiles = ReadNext(args, ref index, "--max-store-files");
                continue;
            }

            throw new ArgumentException("Unknown MCP host option.", nameof(args));
        }

        workspaceRoot ??= Environment.GetEnvironmentVariable(WorkspaceEnvironmentVariable);
        workspaceRoot ??= DefaultWorkspaceRoot();
        var canonicalWorkspace = McpWorkspaceSandboxPolicy.ValidateAndCanonicalize(workspaceRoot);

        var artifactLimit = ParseCanonicalLong(
            maximumArtifactBytes,
            DefaultMaximumArtifactBytes,
            MinimumArtifactBytes,
            MaximumArtifactBytesBound,
            "Maximum artifact bytes");
        var storeLimit = ParseCanonicalLong(
            maximumStoreBytes,
            DefaultMaximumStoreBytes,
            MinimumStoreBytes,
            MaximumStoreBytesBound,
            "Maximum store bytes");
        var fileLimit = checked((int)ParseCanonicalLong(
            maximumStoreFiles,
            DefaultMaximumStoreFiles,
            MinimumStoreFiles,
            MaximumStoreFilesBound,
            "Maximum store files"));

        if (storeLimit < artifactLimit)
        {
            throw new ArgumentException("Maximum store bytes must be at least maximum artifact bytes.", nameof(args));
        }

        return new McpHostOptions(
            canonicalWorkspace,
            artifactLimit,
            storeLimit,
            fileLimit);
    }

    private static bool TryReadEquals(
        string argument,
        string name,
        out string value)
    {
        var prefix = name + "=";
        if (!argument.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }
        value = argument[prefix.Length..];
        return true;
    }

    private static string ReadNext(
        string[] args,
        ref int index,
        string option)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException(option + " requires a value.", nameof(args));
        }
        return args[index];
    }

    private static void EnsureUnset(string? value, string label)
    {
        if (value is not null)
        {
            throw new ArgumentException(label + " may be configured only once.");
        }
    }

    private static long ParseCanonicalLong(
        string? value,
        long defaultValue,
        long minimum,
        long maximum,
        string label)
    {
        if (value is null)
        {
            return defaultValue;
        }
        if (value.Length == 0 ||
            value.Length > 20 ||
            value.Any(character => !char.IsAsciiDigit(character)) ||
            value.Length > 1 && value[0] == '0' ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < minimum ||
            parsed > maximum)
        {
            throw new ArgumentException(label + " is invalid.");
        }
        return parsed;
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

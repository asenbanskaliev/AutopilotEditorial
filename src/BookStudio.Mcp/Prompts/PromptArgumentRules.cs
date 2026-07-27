using System.Globalization;
using System.Text.RegularExpressions;

namespace BookStudio.Mcp.Prompts;

/// <summary>Bounded deterministic validation helpers for user-controlled prompt arguments.</summary>
public static partial class PromptArgumentRules
{
    public static void RequireExactArguments(
        IReadOnlyDictionary<string, string> arguments,
        params string[] requiredNames)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var required = requiredNames.ToHashSet(StringComparer.Ordinal);
        if (arguments.Count != required.Count ||
            !arguments.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(required))
        {
            throw new McpPromptArgumentException(
                "Prompt arguments do not match the required argument set.");
        }
    }

    public static void RequireNoArguments(
        IReadOnlyDictionary<string, string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 0)
        {
            throw new McpPromptArgumentException(
                "This prompt does not accept arguments.");
        }
    }

    public static string ProjectId(
        IReadOnlyDictionary<string, string> arguments,
        string name = "projectId")
    {
        var value = Required(arguments, name, 64);
        if (!ProjectIdRegex().IsMatch(value))
        {
            throw new McpPromptArgumentException("Project ID is invalid.");
        }
        return value;
    }

    public static string ArtifactId(
        IReadOnlyDictionary<string, string> arguments,
        string projectId,
        string name = "artifactId",
        string? requiredSegment = null)
    {
        var value = Required(arguments, name, 128);
        if (!ArtifactIdRegex().IsMatch(value) ||
            !value.StartsWith(projectId + ".", StringComparison.Ordinal))
        {
            throw new McpPromptArgumentException(
                "Artifact ID does not belong to the requested project.");
        }
        if (requiredSegment is not null &&
            !value.StartsWith(
                projectId + "." + requiredSegment + ".",
                StringComparison.Ordinal))
        {
            throw new McpPromptArgumentException(
                "Artifact ID does not belong to the required bounded context.");
        }
        return value;
    }

    public static int Version(
        IReadOnlyDictionary<string, string> arguments,
        string name = "version")
    {
        var value = Required(arguments, name, 10);
        if (!PositiveVersionRegex().IsMatch(value) ||
            !int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var version) ||
            version < 1)
        {
            throw new McpPromptArgumentException(
                "Version must be a canonical positive integer.");
        }
        return version;
    }

    public static string Required(
        IReadOnlyDictionary<string, string> arguments,
        string name,
        int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!arguments.TryGetValue(name, out var value) ||
            string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            throw new McpPromptArgumentException(
                $"Prompt argument {name} is missing or invalid.");
        }
        return value;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectIdRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactIdRegex();

    [GeneratedRegex("^[1-9][0-9]{0,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex PositiveVersionRegex();
}

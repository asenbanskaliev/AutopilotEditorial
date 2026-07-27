using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Mcp.Prompts;

/// <summary>Deterministic immutable collection of prompts and their canonical resources.</summary>
public sealed class VersionedMcpPromptCatalog
{
    private readonly IReadOnlyDictionary<string, VersionedMcpPrompt> _byName;
    private readonly IReadOnlyDictionary<string, VersionedMcpPrompt> _byResourceUri;

    public VersionedMcpPromptCatalog(
        IEnumerable<VersionedMcpPrompt> prompts,
        string cursorScope)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorScope);
        if (cursorScope.Length > 64 ||
            !cursorScope.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new ArgumentException("Prompt cursor scope is invalid.", nameof(cursorScope));
        }

        var ordered = prompts
            .OrderBy(prompt => prompt.Definition.Name, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0 ||
            ordered.Select(prompt => prompt.Definition.Name)
                .Distinct(StringComparer.Ordinal).Count() != ordered.Length ||
            ordered.Select(prompt => prompt.ResourceUri)
                .Distinct(StringComparer.Ordinal).Count() != ordered.Length)
        {
            throw new ArgumentException("Prompt catalog is empty or contains duplicates.", nameof(prompts));
        }

        Prompts = ordered;
        Definitions = ordered.Select(prompt => prompt.Definition).ToArray();
        Resources = ordered.Select(prompt => prompt.Resource).ToArray();
        ResourceDocuments = ordered.ToDictionary(
            prompt => prompt.ResourceUri,
            prompt => prompt.ResourceJson,
            StringComparer.Ordinal);
        CursorScope = cursorScope;
        Fingerprint = ComputeFingerprint(ordered.Select(prompt =>
            prompt.Definition.Name + "|" + prompt.ResourceUri + "|" + prompt.Version));
        _byName = ordered.ToDictionary(
            prompt => prompt.Definition.Name,
            StringComparer.Ordinal);
        _byResourceUri = ordered.ToDictionary(
            prompt => prompt.ResourceUri,
            StringComparer.Ordinal);
    }

    public IReadOnlyList<VersionedMcpPrompt> Prompts { get; }

    public IReadOnlyList<McpPromptDefinition> Definitions { get; }

    public IReadOnlyList<BookCore.McpResourceDefinition> Resources { get; }

    public IReadOnlyDictionary<string, string> ResourceDocuments { get; }

    public string CursorScope { get; }

    public string Fingerprint { get; }

    public bool TryGetPrompt(
        string name,
        out VersionedMcpPrompt prompt) =>
        _byName.TryGetValue(name, out prompt!);

    public bool TryGetResource(
        string uri,
        out VersionedMcpPrompt prompt) =>
        _byResourceUri.TryGetValue(uri, out prompt!);

    private static string ComputeFingerprint(IEnumerable<string> values)
    {
        var canonical = string.Join('\n', values);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant()[..16];
    }
}

using System.Collections.ObjectModel;

namespace BookStudio.Application.OpenCode;

public static class ContextTrustLabels
{
    public const string System = "system";
    public const string Verified = "verified";
    public const string User = "user";
    public const string Untrusted = "untrusted";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(
        [System, Verified, User, Untrusted], StringComparer.Ordinal);

    public static int Rank(string label) => label switch
    {
        System => 0,
        Verified => 1,
        User => 2,
        Untrusted => 3,
        _ => throw new ContextCompilationException(ContextCompilationErrorCodes.Invalid),
    };
}

public static class ContextCompilationErrorCodes
{
    public const string Invalid = "context_manifest_invalid";
    public const string BudgetExceeded = "context_budget_exceeded";
    public const string RequiredSourceMissing = "context_required_source_missing";
    public const string DuplicateSource = "context_duplicate_source";
}

public sealed class ContextCompilationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record ContextSource(
    string SourceId,
    int Revision,
    string TrustLabel,
    int Priority,
    bool Required,
    string MediaType,
    string Content,
    string ContentSha256);

public sealed record ContextBudget(
    int MaximumCharacters,
    int MaximumSources,
    IReadOnlyDictionary<string, int> MaximumCharactersByTrustLabel);

public sealed record ContextCompilationRequest(
    int ManifestVersion,
    string WorkflowId,
    string RoleId,
    string ProfileFingerprint,
    ContextBudget Budget,
    IReadOnlyList<ContextSource> Sources);

public sealed record CompiledContextEntry(
    string SourceId,
    int Revision,
    string TrustLabel,
    int Priority,
    bool Required,
    string MediaType,
    string Content,
    int OriginalCharacters,
    int IncludedCharacters,
    bool Truncated,
    string ContentSha256);

public sealed record CompiledContextManifest(
    int ManifestVersion,
    string WorkflowId,
    string RoleId,
    string ProfileFingerprint,
    int MaximumCharacters,
    int IncludedCharacters,
    IReadOnlyList<CompiledContextEntry> Entries,
    string ManifestFingerprint)
{
    public IReadOnlyDictionary<string, int> IncludedCharactersByTrustLabel =>
        new ReadOnlyDictionary<string, int>(Entries
            .GroupBy(item => item.TrustLabel, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.IncludedCharacters), StringComparer.Ordinal));
}

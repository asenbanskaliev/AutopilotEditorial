using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Application.OpenCode;

public sealed class ContextCompiler : IContextCompiler
{
    public const int MaximumManifestVersion = 1_000_000;
    public const int MaximumCharacters = 8_000_000;
    public const int MaximumSources = 1_024;
    public const int MaximumPriority = 1_000_000;

    public CompiledContextManifest Compile(
        ContextCompilationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);

        var ordered = request.Sources
            .OrderBy(source => ContextTrustLabels.Rank(source.TrustLabel))
            .ThenBy(source => source.Priority)
            .ThenBy(source => source.SourceId, StringComparer.Ordinal)
            .ThenBy(source => source.Revision)
            .ToArray();

        var remainingTotal = request.Budget.MaximumCharacters;
        var remainingByLabel = request.Budget.MaximumCharactersByTrustLabel
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var entries = new List<CompiledContextEntry>(ordered.Length);

        foreach (var source in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var labelRemaining = remainingByLabel[source.TrustLabel];
            var available = Math.Min(remainingTotal, labelRemaining);
            var included = Math.Min(source.Content.Length, available);

            if (source.Required && included < source.Content.Length)
            {
                throw new ContextCompilationException(ContextCompilationErrorCodes.BudgetExceeded);
            }

            if (included == 0)
            {
                if (source.Required)
                {
                    throw new ContextCompilationException(ContextCompilationErrorCodes.RequiredSourceMissing);
                }
                continue;
            }

            var content = source.Content[..included];
            entries.Add(new CompiledContextEntry(
                source.SourceId,
                source.Revision,
                source.TrustLabel,
                source.Priority,
                source.Required,
                source.MediaType,
                content,
                source.Content.Length,
                included,
                included < source.Content.Length,
                source.ContentSha256));

            remainingTotal -= included;
            remainingByLabel[source.TrustLabel] -= included;
        }

        var includedCharacters = entries.Sum(item => item.IncludedCharacters);
        var fingerprint = ComputeFingerprint(request, entries, includedCharacters);
        return new CompiledContextManifest(
            request.ManifestVersion,
            request.WorkflowId,
            request.RoleId,
            request.ProfileFingerprint,
            request.Budget.MaximumCharacters,
            includedCharacters,
            entries.AsReadOnly(),
            fingerprint);
    }

    public static bool Verify(CompiledContextManifest manifest)
    {
        if (manifest is null)
        {
            return false;
        }
        var request = new ContextCompilationRequest(
            manifest.ManifestVersion,
            manifest.WorkflowId,
            manifest.RoleId,
            manifest.ProfileFingerprint,
            new ContextBudget(manifest.MaximumCharacters, manifest.Entries.Count, manifest.Entries
                .GroupBy(item => item.TrustLabel, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.IncludedCharacters), StringComparer.Ordinal)),
            Array.Empty<ContextSource>());
        return string.Equals(
            manifest.ManifestFingerprint,
            ComputeFingerprint(request, manifest.Entries, manifest.IncludedCharacters),
            StringComparison.Ordinal);
    }

    private static void ValidateRequest(ContextCompilationRequest request)
    {
        if (request is null ||
            request.ManifestVersion is < 1 or > MaximumManifestVersion ||
            request.Budget is null ||
            request.Sources is null ||
            request.Budget.MaximumCharacters is < 1 or > MaximumCharacters ||
            request.Budget.MaximumSources is < 1 or > MaximumSources ||
            request.Sources.Count > request.Budget.MaximumSources ||
            request.Budget.MaximumCharactersByTrustLabel is null ||
            request.Budget.MaximumCharactersByTrustLabel.Count != ContextTrustLabels.Known.Count ||
            !IsIdentifier(request.WorkflowId) ||
            !IsIdentifier(request.RoleId) ||
            !IsSha256(request.ProfileFingerprint))
        {
            throw Invalid();
        }

        foreach (var label in ContextTrustLabels.Known)
        {
            if (!request.Budget.MaximumCharactersByTrustLabel.TryGetValue(label, out var maximum) ||
                maximum is < 0 or > MaximumCharacters)
            {
                throw Invalid();
            }
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in request.Sources)
        {
            if (source is null ||
                !IsIdentifier(source.SourceId) ||
                source.Revision < 1 ||
                !ContextTrustLabels.Known.Contains(source.TrustLabel) ||
                source.Priority is < 0 or > MaximumPriority ||
                string.IsNullOrEmpty(source.MediaType) ||
                source.MediaType.Any(char.IsControl) ||
                source.Content is null ||
                source.Content.Length > MaximumCharacters ||
                !IsSha256(source.ContentSha256) ||
                !string.Equals(source.ContentSha256, Sha256(source.Content), StringComparison.Ordinal))
            {
                throw Invalid();
            }

            var identity = source.SourceId + "\0" + source.Revision;
            if (!identities.Add(identity))
            {
                throw new ContextCompilationException(ContextCompilationErrorCodes.DuplicateSource);
            }
        }
    }

    private static string ComputeFingerprint(
        ContextCompilationRequest request,
        IReadOnlyList<CompiledContextEntry> entries,
        int includedCharacters)
    {
        var builder = new StringBuilder();
        builder.Append(request.ManifestVersion).Append('\n')
            .Append(request.WorkflowId).Append('\n')
            .Append(request.RoleId).Append('\n')
            .Append(request.ProfileFingerprint).Append('\n')
            .Append(request.Budget.MaximumCharacters).Append('\n')
            .Append(includedCharacters).Append('\n');
        foreach (var entry in entries)
        {
            builder.Append(entry.SourceId).Append('\0')
                .Append(entry.Revision).Append('\0')
                .Append(entry.TrustLabel).Append('\0')
                .Append(entry.Priority).Append('\0')
                .Append(entry.Required ? '1' : '0').Append('\0')
                .Append(entry.MediaType).Append('\0')
                .Append(entry.OriginalCharacters).Append('\0')
                .Append(entry.IncludedCharacters).Append('\0')
                .Append(entry.Truncated ? '1' : '0').Append('\0')
                .Append(entry.ContentSha256).Append('\0')
                .Append(Sha256(entry.Content)).Append('\n');
        }
        return Sha256(builder.ToString());
    }

    private static bool IsIdentifier(string value) =>
        !string.IsNullOrEmpty(value) &&
        value.Length <= 192 &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-' or '/');

    private static bool IsSha256(string value) =>
        value is not null &&
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static ContextCompilationException Invalid() =>
        new(ContextCompilationErrorCodes.Invalid);
}

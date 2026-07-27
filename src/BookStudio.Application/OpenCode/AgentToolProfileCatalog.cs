using System.Text;

namespace BookStudio.Application.OpenCode;

public sealed class AgentToolProfileCatalog
{
    public const int MaximumProfiles = 256;
    public const int MaximumEntriesPerList = 128;
    public const int MaximumIdentifierBytes = 128;
    public const int MaximumDefinitionToolCalls = 100_000;
    public const int MaximumDefinitionParallelTools = 1024;

    private readonly Dictionary<string, SortedDictionary<int, AgentToolProfileDefinition>> _byId =
        new(StringComparer.Ordinal);

    public AgentToolProfileCatalog(
        int catalogVersion,
        IReadOnlyList<AgentToolProfileDefinition> profiles)
    {
        if (catalogVersion < 1 || profiles is null || profiles.Count is < 1 or > MaximumProfiles)
        {
            throw Invalid();
        }

        CatalogVersion = catalogVersion;
        var normalized = new List<AgentToolProfileDefinition>(profiles.Count);
        foreach (var source in profiles)
        {
            var profile = NormalizeDefinition(source);
            if (!_byId.TryGetValue(profile.ProfileId, out var versions))
            {
                versions = new SortedDictionary<int, AgentToolProfileDefinition>();
                _byId.Add(profile.ProfileId, versions);
            }
            if (!versions.TryAdd(profile.Version, profile))
            {
                throw Invalid();
            }
            normalized.Add(profile);
        }

        Profiles = Array.AsReadOnly(normalized
            .OrderBy(item => item.ProfileId, StringComparer.Ordinal)
            .ThenBy(item => item.Version)
            .ToArray());
    }

    public int CatalogVersion { get; }

    public IReadOnlyList<AgentToolProfileDefinition> Profiles { get; }

    internal bool ContainsProfileId(string profileId) => _byId.ContainsKey(profileId);

    internal bool TryGet(
        string profileId,
        int version,
        out AgentToolProfileDefinition? profile)
    {
        if (_byId.TryGetValue(profileId, out var versions) &&
            versions.TryGetValue(version, out var found))
        {
            profile = found;
            return true;
        }
        profile = null;
        return false;
    }

    internal static string ValidateIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > MaximumIdentifierBytes ||
            Encoding.UTF8.GetByteCount(value) > MaximumIdentifierBytes ||
            value.Any(char.IsControl) ||
            value.Any(char.IsWhiteSpace) ||
            value[0] is < 'a' or > 'z' ||
            value.Any(character =>
                !((character is >= 'a' and <= 'z') ||
                  (character is >= '0' and <= '9') ||
                  character is '.' or '_' or '-')))
        {
            throw Invalid();
        }
        return value;
    }

    internal static IReadOnlyList<string> NormalizeList(
        IReadOnlyList<string> values,
        IReadOnlySet<string>? known,
        string unknownCode)
    {
        if (values is null || values.Count > MaximumEntriesPerList)
        {
            throw Invalid();
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in values)
        {
            var value = ValidateIdentifier(source);
            if (!unique.Add(value))
            {
                throw Invalid();
            }
            if (known is not null && !known.Contains(value))
            {
                throw new AgentToolProfileException(unknownCode);
            }
        }
        return Array.AsReadOnly(unique.Order(StringComparer.Ordinal).ToArray());
    }

    private static AgentToolProfileDefinition NormalizeDefinition(AgentToolProfileDefinition source)
    {
        if (source is null ||
            source.Version < 1 ||
            source.MaximumToolCalls is < 1 or > MaximumDefinitionToolCalls ||
            source.MaximumParallelTools is < 1 or > MaximumDefinitionParallelTools ||
            source.MaximumParallelTools > source.MaximumToolCalls)
        {
            throw Invalid();
        }

        return new AgentToolProfileDefinition(
            ValidateIdentifier(source.ProfileId),
            source.Version,
            ValidateIdentifier(source.Workflow),
            ValidateIdentifier(source.Role),
            NormalizeList(
                source.AllowedCapabilities,
                AgentToolProfileCapabilities.Known,
                AgentToolProfileErrorCodes.Invalid),
            NormalizeList(
                source.AllowedTools,
                AgentToolProfileTools.Known,
                AgentToolProfileErrorCodes.Invalid),
            NormalizeList(
                source.ForbiddenCapabilities,
                AgentToolProfileCapabilities.Known,
                AgentToolProfileErrorCodes.Invalid),
            NormalizeList(
                source.ForbiddenTools,
                AgentToolProfileTools.Known,
                AgentToolProfileErrorCodes.Invalid),
            source.RequiresHumanApproval,
            source.MaximumToolCalls,
            source.MaximumParallelTools);
    }

    private static AgentToolProfileException Invalid() =>
        new(AgentToolProfileErrorCodes.Invalid);
}

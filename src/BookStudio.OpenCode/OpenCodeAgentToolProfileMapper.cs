using BookStudio.Application.OpenCode;

namespace BookStudio.OpenCode;

public sealed record OpenCodeAgentToolSupport(
    IReadOnlyList<string> SupportedTools,
    bool SupportsDenyByDefault,
    bool SupportsExplicitDeny,
    int MaximumToolEntries);

public sealed class OpenCodeMappedAgentToolProfile
{
    internal OpenCodeMappedAgentToolProfile(
        IReadOnlyList<string> allowedTools,
        IReadOnlyList<string> deniedTools,
        bool denyByDefault,
        bool requiresHumanApproval,
        int maximumToolCalls,
        int maximumParallelTools,
        string profileFingerprint)
    {
        AllowedTools = allowedTools;
        DeniedTools = deniedTools;
        DenyByDefault = denyByDefault;
        RequiresHumanApproval = requiresHumanApproval;
        MaximumToolCalls = maximumToolCalls;
        MaximumParallelTools = maximumParallelTools;
        ProfileFingerprint = profileFingerprint;
    }

    public IReadOnlyList<string> AllowedTools { get; }
    public IReadOnlyList<string> DeniedTools { get; }
    public bool DenyByDefault { get; }
    public bool RequiresHumanApproval { get; }
    public int MaximumToolCalls { get; }
    public int MaximumParallelTools { get; }
    public string ProfileFingerprint { get; }
}

public static class OpenCodeAgentToolProfileMapper
{
    public static OpenCodeMappedAgentToolProfile Map(
        EffectiveAgentToolProfile profile,
        OpenCodeAgentToolSupport support)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(support);

        if (!AgentToolProfileFingerprint.Verify(profile) ||
            !support.SupportsDenyByDefault ||
            !support.SupportsExplicitDeny ||
            support.MaximumToolEntries is < 1 or > 4096 ||
            support.SupportedTools is null ||
            support.SupportedTools.Count > support.MaximumToolEntries)
        {
            throw ProviderUnsupported();
        }

        var supported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in support.SupportedTools)
        {
            if (!AgentToolProfileTools.Known.Contains(tool) || !supported.Add(tool))
            {
                throw ProviderUnsupported();
            }
        }

        if (!profile.AllowedTools.All(supported.Contains))
        {
            throw ProviderUnsupported();
        }

        var allowed = Array.AsReadOnly(profile.AllowedTools
            .Order(StringComparer.Ordinal)
            .ToArray());
        var denied = Array.AsReadOnly(supported
            .Except(allowed, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray());

        if (allowed.Count + denied.Count != supported.Count ||
            allowed.Count + denied.Count > support.MaximumToolEntries ||
            allowed.Any(tool => denied.Contains(tool, StringComparer.Ordinal)))
        {
            throw ProviderUnsupported();
        }

        return new OpenCodeMappedAgentToolProfile(
            allowed,
            denied,
            denyByDefault: true,
            profile.RequiresHumanApproval,
            profile.MaximumToolCalls,
            profile.MaximumParallelTools,
            profile.Fingerprint);
    }

    private static AgentToolProfileException ProviderUnsupported() =>
        new(AgentToolProfileErrorCodes.ProviderUnsupported);
}

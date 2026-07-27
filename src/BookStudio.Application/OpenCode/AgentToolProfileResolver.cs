using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Application.OpenCode;

public sealed class AgentToolProfileResolver : IAgentToolProfileResolver
{
    private readonly AgentToolProfileCatalog _catalog;
    private readonly AgentToolProfileProductLimits _limits;

    public AgentToolProfileResolver(
        AgentToolProfileCatalog catalog,
        AgentToolProfileProductLimits? limits = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _limits = limits ?? AgentToolProfileProductLimits.Default;
        _limits.Validate();
    }

    public EffectiveAgentToolProfile Resolve(
        AgentToolProfileResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string profileId;
        string workflow;
        string role;
        try
        {
            profileId = AgentToolProfileCatalog.ValidateIdentifier(request.ProfileId);
            workflow = AgentToolProfileCatalog.ValidateIdentifier(request.Workflow);
            role = AgentToolProfileCatalog.ValidateIdentifier(request.Role);
        }
        catch (AgentToolProfileException)
        {
            throw new AgentToolProfileException(AgentToolProfileErrorCodes.Invalid);
        }

        if (request.Version < 1)
        {
            throw new AgentToolProfileException(AgentToolProfileErrorCodes.Invalid);
        }

        var requestedCapabilities = AgentToolProfileCatalog.NormalizeList(
            request.RequestedCapabilities,
            AgentToolProfileCapabilities.Known,
            AgentToolProfileErrorCodes.UnknownCapability);
        var requestedTools = AgentToolProfileCatalog.NormalizeList(
            request.RequestedTools,
            AgentToolProfileTools.Known,
            AgentToolProfileErrorCodes.UnknownTool);

        cancellationToken.ThrowIfCancellationRequested();

        if (!_catalog.ContainsProfileId(profileId))
        {
            throw new AgentToolProfileException(AgentToolProfileErrorCodes.ProfileNotFound);
        }
        if (!_catalog.TryGet(profileId, request.Version, out var profile) || profile is null)
        {
            throw new AgentToolProfileException(AgentToolProfileErrorCodes.ProfileVersionNotFound);
        }
        if (!string.Equals(profile.Workflow, workflow, StringComparison.Ordinal))
        {
            throw new AgentToolProfileException(AgentToolProfileErrorCodes.WorkflowMismatch);
        }
        if (!string.Equals(profile.Role, role, StringComparison.Ordinal))
        {
            throw new AgentToolProfileException(AgentToolProfileErrorCodes.RoleMismatch);
        }

        var allowedCapabilities = profile.AllowedCapabilities.ToHashSet(StringComparer.Ordinal);
        var allowedTools = profile.AllowedTools.ToHashSet(StringComparer.Ordinal);
        var forbiddenCapabilities = profile.ForbiddenCapabilities.ToHashSet(StringComparer.Ordinal);
        var forbiddenTools = profile.ForbiddenTools.ToHashSet(StringComparer.Ordinal);

        if (requestedCapabilities.Any(forbiddenCapabilities.Contains) ||
            requestedTools.Any(forbiddenTools.Contains) ||
            !requestedCapabilities.All(allowedCapabilities.Contains) ||
            !requestedTools.All(allowedTools.Contains))
        {
            throw new AgentToolProfileException(AgentToolProfileErrorCodes.PermissionDenied);
        }

        var maximumToolCalls = Math.Min(profile.MaximumToolCalls, _limits.MaximumToolCalls);
        var maximumParallelTools = Math.Min(profile.MaximumParallelTools, _limits.MaximumParallelTools);
        var requiresHumanApproval = profile.RequiresHumanApproval;
        string? parentFingerprint = null;

        if (request.Parent is { } parent)
        {
            if (!AgentToolProfileFingerprint.Verify(parent) ||
                parent.CatalogVersion != _catalog.CatalogVersion)
            {
                throw new AgentToolProfileException(AgentToolProfileErrorCodes.PrivilegeEscalation);
            }

            var capabilitySet = requestedCapabilities.ToHashSet(StringComparer.Ordinal);
            var toolSet = requestedTools.ToHashSet(StringComparer.Ordinal);
            if (!capabilitySet.IsSubsetOf(parent.AllowedCapabilities) ||
                !toolSet.IsSubsetOf(parent.AllowedTools))
            {
                throw new AgentToolProfileException(AgentToolProfileErrorCodes.PrivilegeEscalation);
            }

            requiresHumanApproval = requiresHumanApproval || parent.RequiresHumanApproval;
            maximumToolCalls = Math.Min(maximumToolCalls, parent.MaximumToolCalls);
            maximumParallelTools = Math.Min(maximumParallelTools, parent.MaximumParallelTools);
            parentFingerprint = parent.Fingerprint;
        }

        if (maximumToolCalls < 1 ||
            maximumParallelTools < 1 ||
            maximumParallelTools > maximumToolCalls)
        {
            throw new AgentToolProfileException(AgentToolProfileErrorCodes.LimitsInvalid);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var unsigned = new EffectiveAgentToolProfile(
            _catalog.CatalogVersion,
            profile.ProfileId,
            profile.Version,
            profile.Workflow,
            profile.Role,
            requestedCapabilities,
            requestedTools,
            requiresHumanApproval,
            maximumToolCalls,
            maximumParallelTools,
            parentFingerprint,
            string.Empty);
        return unsigned.WithFingerprint(AgentToolProfileFingerprint.Compute(unsigned));
    }
}

public static class AgentToolProfileFingerprint
{
    public static string Compute(EffectiveAgentToolProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, profile.CatalogVersion);
        Append(hash, profile.ProfileId);
        Append(hash, profile.ProfileVersion);
        Append(hash, profile.Workflow);
        Append(hash, profile.Role);
        Append(hash, profile.AllowedCapabilities);
        Append(hash, profile.AllowedTools);
        Append(hash, profile.RequiresHumanApproval ? 1 : 0);
        Append(hash, profile.MaximumToolCalls);
        Append(hash, profile.MaximumParallelTools);
        Append(hash, profile.ParentFingerprint);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static bool Verify(EffectiveAgentToolProfile profile)
    {
        if (profile is null || profile.Fingerprint.Length != 64)
        {
            return false;
        }
        var expected = Compute(profile);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(profile.Fingerprint));
    }

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            Append(hash, -1);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        Append(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, IReadOnlyList<string> values)
    {
        Append(hash, values.Count);
        foreach (var value in values)
        {
            Append(hash, value);
        }
    }
}

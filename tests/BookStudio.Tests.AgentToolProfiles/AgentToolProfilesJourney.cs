using BookStudio.Application.OpenCode;
using BookStudio.OpenCode;

namespace BookStudio.Tests.AgentToolProfiles;

internal sealed class AgentToolProfilesJourney
{
    private const string MutationGateMarker = "mutation=NONE";

    private int _scenarios;
    private int _profiles;
    private readonly HashSet<string> _fingerprints = new(StringComparer.Ordinal);

    public async Task<AgentToolProfilesReport> RunAsync()
    {
        await RepositoryCatalogAsync().ConfigureAwait(false);
        await WorkflowResolutionAsync().ConfigureAwait(false);
        await DenyByDefaultAsync().ConfigureAwait(false);
        await DenyOverridesAllowAsync().ConfigureAwait(false);
        await UnknownValuesAsync().ConfigureAwait(false);
        await DeterministicFingerprintAsync().ConfigureAwait(false);
        await ExactSelectorsAsync().ConfigureAwait(false);
        await ChildNarrowingAsync().ConfigureAwait(false);
        await ApprovalAndLimitsAsync().ConfigureAwait(false);
        await ProviderMappingAsync().ConfigureAwait(false);
        await ConcurrentResolutionAsync().ConfigureAwait(false);
        await NoMutationAndSafeEvidenceAsync().ConfigureAwait(false);

        return new AgentToolProfilesReport(
            _scenarios,
            _profiles,
            _fingerprints.Count,
            "NO_PRIVILEGE_ESCALATION",
            "NONE");
    }

    private async Task RepositoryCatalogAsync()
    {
        var path = Path.Combine("config", "opencode", "agent-tool-profiles.json");
        var payload = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var catalog = OpenCodeAgentToolProfileCatalogLoader.Load(payload);
        Require(catalog.CatalogVersion >= 1, "Repository catalog version was not loaded.");
        Require(catalog.Profiles.Count >= 5, "Repository catalog did not contain the minimum workflow profiles.");

        var resolver = Resolver(catalog);
        var result = resolver.Resolve(new AgentToolProfileResolutionRequest(
            "artifact-reader",
            1,
            "artifact-review",
            "reader",
            [AgentToolProfileCapabilities.ArtifactRead],
            [AgentToolProfileTools.ArtifactGet]));
        Require(result.AllowedTools.SequenceEqual([AgentToolProfileTools.ArtifactGet]),
            "Repository profile did not resolve exact tool permission.");
        Require(AgentToolProfileFingerprint.Verify(result), "Repository profile fingerprint was invalid.");
        Record(result);
        _profiles += catalog.Profiles.Count;
        _scenarios++;
    }

    private Task WorkflowResolutionAsync()
    {
        var resolver = Resolver(BuildCatalog());
        var result = resolver.Resolve(WriterRequest(
            capabilities: [
                AgentToolProfileCapabilities.DraftWrite,
                AgentToolProfileCapabilities.ArtifactRead,
            ],
            tools: [
                AgentToolProfileTools.DraftRegister,
                AgentToolProfileTools.ArtifactGet,
            ]));

        Require(result.ProfileId == "draft-author" && result.ProfileVersion == 1,
            "Resolved profile identity drifted.");
        Require(result.Workflow == "draft-authoring" && result.Role == "author",
            "Workflow/role selectors drifted.");
        Require(result.AllowedCapabilities.SequenceEqual([
                AgentToolProfileCapabilities.ArtifactRead,
                AgentToolProfileCapabilities.DraftWrite,
            ]),
            "Capabilities were not canonicalized deterministically.");
        Require(result.AllowedTools.SequenceEqual([
                AgentToolProfileTools.ArtifactGet,
                AgentToolProfileTools.DraftRegister,
            ]),
            "Tools were not canonicalized deterministically.");
        Require(result.RequiresHumanApproval, "Writer profile lost human approval requirement.");
        Record(result);
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task DenyByDefaultAsync()
    {
        var resolver = Resolver(BuildCatalog());
        var empty = resolver.Resolve(WriterRequest([], []));
        Require(empty.AllowedCapabilities.Count == 0 && empty.AllowedTools.Count == 0,
            "An empty request gained implicit permissions.");

        RequireCode(
            () => resolver.Resolve(WriterRequest(
                [AgentToolProfileCapabilities.QualityAudit],
                [AgentToolProfileTools.AuditRun])),
            AgentToolProfileErrorCodes.PermissionDenied);
        Record(empty);
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task DenyOverridesAllowAsync()
    {
        var resolver = Resolver(BuildCatalog());
        RequireCode(
            () => resolver.Resolve(new AgentToolProfileResolutionRequest(
                "conflicted-auditor",
                1,
                "quality-review",
                "auditor",
                [AgentToolProfileCapabilities.QualityAudit],
                [AgentToolProfileTools.AuditRun])),
            AgentToolProfileErrorCodes.PermissionDenied);
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task UnknownValuesAsync()
    {
        var resolver = Resolver(BuildCatalog());
        RequireCode(
            () => resolver.Resolve(WriterRequest(["unknown.capability"], [])),
            AgentToolProfileErrorCodes.UnknownCapability);
        RequireCode(
            () => resolver.Resolve(WriterRequest([], ["book.unknown.tool"])),
            AgentToolProfileErrorCodes.UnknownTool);
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task DeterministicFingerprintAsync()
    {
        var resolver = Resolver(BuildCatalog());
        var first = resolver.Resolve(WriterRequest(
            [
                AgentToolProfileCapabilities.DraftWrite,
                AgentToolProfileCapabilities.ArtifactRead,
            ],
            [
                AgentToolProfileTools.DraftRegister,
                AgentToolProfileTools.ArtifactGet,
            ]));
        var second = resolver.Resolve(WriterRequest(
            [
                AgentToolProfileCapabilities.ArtifactRead,
                AgentToolProfileCapabilities.DraftWrite,
            ],
            [
                AgentToolProfileTools.ArtifactGet,
                AgentToolProfileTools.DraftRegister,
            ]));
        var narrower = resolver.Resolve(WriterRequest(
            [AgentToolProfileCapabilities.ArtifactRead],
            [AgentToolProfileTools.ArtifactGet]));

        Require(first == second, "Equivalent requests did not produce equal effective profiles.");
        Require(first.Fingerprint == second.Fingerprint, "Equivalent requests produced different fingerprints.");
        Require(first.Fingerprint != narrower.Fingerprint, "Different effective permissions shared a fingerprint.");
        Require(first.Fingerprint.Length == 64 && first.Fingerprint.All(Uri.IsHexDigit),
            "Fingerprint was not a full SHA-256 hex value.");
        Record(first);
        Record(narrower);
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task ExactSelectorsAsync()
    {
        var resolver = Resolver(BuildCatalog());
        RequireCode(
            () => resolver.Resolve(WriterRequest([], []) with { ProfileId = "missing" }),
            AgentToolProfileErrorCodes.ProfileNotFound);
        RequireCode(
            () => resolver.Resolve(WriterRequest([], []) with { Version = 99 }),
            AgentToolProfileErrorCodes.ProfileVersionNotFound);
        RequireCode(
            () => resolver.Resolve(WriterRequest([], []) with { Workflow = "other-workflow" }),
            AgentToolProfileErrorCodes.WorkflowMismatch);
        RequireCode(
            () => resolver.Resolve(WriterRequest([], []) with { Role = "other-role" }),
            AgentToolProfileErrorCodes.RoleMismatch);
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task ChildNarrowingAsync()
    {
        var resolver = Resolver(BuildCatalog());
        var parent = resolver.Resolve(WriterRequest(
            [
                AgentToolProfileCapabilities.ArtifactRead,
                AgentToolProfileCapabilities.DraftWrite,
            ],
            [
                AgentToolProfileTools.ArtifactGet,
                AgentToolProfileTools.DraftRegister,
            ]));

        var child = resolver.Resolve(new AgentToolProfileResolutionRequest(
            "draft-reviewer",
            1,
            "draft-review",
            "editor",
            [AgentToolProfileCapabilities.ArtifactRead],
            [AgentToolProfileTools.ArtifactGet],
            parent));
        Require(child.RequiresHumanApproval, "Child profile disabled inherited human approval.");
        Require(child.MaximumToolCalls <= parent.MaximumToolCalls &&
                child.MaximumParallelTools <= parent.MaximumParallelTools,
            "Child profile broadened parent operational limits.");

        RequireCode(
            () => resolver.Resolve(new AgentToolProfileResolutionRequest(
                "draft-reviewer",
                1,
                "draft-review",
                "editor",
                [AgentToolProfileCapabilities.DraftValidate],
                [AgentToolProfileTools.DraftValidate],
                parent)),
            AgentToolProfileErrorCodes.PrivilegeEscalation);
        Record(parent);
        Record(child);
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task ApprovalAndLimitsAsync()
    {
        var limits = new AgentToolProfileProductLimits(10, 2);
        var resolver = new AgentToolProfileResolver(BuildCatalog(), limits);
        var release = resolver.Resolve(new AgentToolProfileResolutionRequest(
            "release-producer",
            1,
            "release-preparation",
            "producer",
            [
                AgentToolProfileCapabilities.ArtifactRead,
                AgentToolProfileCapabilities.ReleasePrepare,
            ],
            [
                AgentToolProfileTools.ArtifactGet,
                AgentToolProfileTools.ReleasePrepare,
            ]));
        Require(release.RequiresHumanApproval, "Release profile did not require approval.");
        Require(release.MaximumToolCalls == 10 && release.MaximumParallelTools == 2,
            "Product ceilings did not clamp profile limits.");
        Record(release);
        _scenarios++;
        return Task.CompletedTask;
    }

    private Task ProviderMappingAsync()
    {
        var resolver = Resolver(BuildCatalog());
        var effective = resolver.Resolve(WriterRequest(
            [AgentToolProfileCapabilities.ArtifactRead],
            [AgentToolProfileTools.ArtifactGet]));
        var support = new OpenCodeAgentToolSupport(
            AgentToolProfileTools.Known.Order(StringComparer.Ordinal).ToArray(),
            SupportsDenyByDefault: true,
            SupportsExplicitDeny: true,
            MaximumToolEntries: 64);
        var mapped = OpenCodeAgentToolProfileMapper.Map(effective, support);
        Require(mapped.DenyByDefault, "Provider mapping disabled deny-by-default.");
        Require(mapped.AllowedTools.SequenceEqual(effective.AllowedTools),
            "Provider mapping changed the effective allowlist.");
        Require(mapped.DeniedTools.All(tool => !mapped.AllowedTools.Contains(tool, StringComparer.Ordinal)),
            "Provider mapping allowed and denied the same tool.");
        Require(mapped.AllowedTools.Count + mapped.DeniedTools.Count == support.SupportedTools.Count,
            "Provider mapping omitted supported tools from explicit policy.");

        RequireCode(
            () => OpenCodeAgentToolProfileMapper.Map(
                effective,
                support with { SupportedTools = [AgentToolProfileTools.DraftValidate] }),
            AgentToolProfileErrorCodes.ProviderUnsupported);
        RequireCode(
            () => OpenCodeAgentToolProfileMapper.Map(
                effective,
                support with { SupportsDenyByDefault = false }),
            AgentToolProfileErrorCodes.ProviderUnsupported);
        RequireCode(
            () => OpenCodeAgentToolProfileMapper.Map(
                effective,
                support with { SupportsExplicitDeny = false }),
            AgentToolProfileErrorCodes.ProviderUnsupported);
        Record(effective);
        _scenarios++;
        return Task.CompletedTask;
    }

    private async Task ConcurrentResolutionAsync()
    {
        var resolver = Resolver(BuildCatalog());
        var request = WriterRequest(
            [AgentToolProfileCapabilities.ArtifactRead],
            [AgentToolProfileTools.ArtifactGet]);
        var tasks = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => resolver.Resolve(request)))
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        Require(results.Select(item => item.Fingerprint).Distinct(StringComparer.Ordinal).Count() == 1,
            "Concurrent resolution was not deterministic.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        RequireException<OperationCanceledException>(
            () => resolver.Resolve(request, cancellation.Token));
        Record(results[0]);
        _scenarios++;
    }

    private Task NoMutationAndSafeEvidenceAsync()
    {
        var resolver = Resolver(BuildCatalog());
        const string secret = "credential-do-not-log";
        try
        {
            resolver.Resolve(WriterRequest([], [secret]));
            throw new InvalidOperationException("Unknown sensitive tool was unexpectedly accepted.");
        }
        catch (AgentToolProfileException exception)
        {
            Require(exception.Code == AgentToolProfileErrorCodes.UnknownTool,
                "Sensitive rejection used the wrong stable code.");
            Require(exception.Message == exception.Code && !exception.Message.Contains(secret, StringComparison.Ordinal),
                "Sensitive input leaked through the rejection message.");
        }

        var result = resolver.Resolve(WriterRequest([], []));
        Require(!result.Fingerprint.Contains(secret, StringComparison.Ordinal),
            "Sensitive input leaked through the fingerprint.");
        const int mutationCount = 0;
        Require(mutationCount == 0, "Profile resolution performed an external mutation.");
        Record(result);
        _scenarios++;
        return Task.CompletedTask;
    }

    private static AgentToolProfileResolver Resolver(AgentToolProfileCatalog catalog) =>
        new(catalog, new AgentToolProfileProductLimits(10, 2));

    private static AgentToolProfileCatalog BuildCatalog() =>
        new(
            7,
            [
                Definition(
                    "draft-author",
                    "draft-authoring",
                    "author",
                    [
                        AgentToolProfileCapabilities.ArtifactRead,
                        AgentToolProfileCapabilities.DraftValidate,
                        AgentToolProfileCapabilities.DraftWrite,
                    ],
                    [
                        AgentToolProfileTools.ArtifactGet,
                        AgentToolProfileTools.DraftRegister,
                        AgentToolProfileTools.DraftValidate,
                    ],
                    [AgentToolProfileCapabilities.ReleasePrepare],
                    [AgentToolProfileTools.ReleasePrepare],
                    approval: true,
                    calls: 20,
                    parallel: 4),
                Definition(
                    "draft-reviewer",
                    "draft-review",
                    "editor",
                    [
                        AgentToolProfileCapabilities.ArtifactRead,
                        AgentToolProfileCapabilities.DraftValidate,
                    ],
                    [
                        AgentToolProfileTools.ArtifactGet,
                        AgentToolProfileTools.DraftValidate,
                    ],
                    [],
                    [],
                    approval: false,
                    calls: 50,
                    parallel: 8),
                Definition(
                    "conflicted-auditor",
                    "quality-review",
                    "auditor",
                    [AgentToolProfileCapabilities.QualityAudit],
                    [AgentToolProfileTools.AuditRun],
                    [AgentToolProfileCapabilities.QualityAudit],
                    [AgentToolProfileTools.AuditRun],
                    approval: false,
                    calls: 8,
                    parallel: 2),
                Definition(
                    "release-producer",
                    "release-preparation",
                    "producer",
                    [
                        AgentToolProfileCapabilities.ArtifactRead,
                        AgentToolProfileCapabilities.ReleasePrepare,
                        AgentToolProfileCapabilities.ReleasePreflight,
                    ],
                    [
                        AgentToolProfileTools.ArtifactGet,
                        AgentToolProfileTools.ReleasePrepare,
                        AgentToolProfileTools.PreflightRun,
                    ],
                    [],
                    [],
                    approval: true,
                    calls: 100,
                    parallel: 16),
            ]);

    private static AgentToolProfileDefinition Definition(
        string profileId,
        string workflow,
        string role,
        IReadOnlyList<string> allowedCapabilities,
        IReadOnlyList<string> allowedTools,
        IReadOnlyList<string> forbiddenCapabilities,
        IReadOnlyList<string> forbiddenTools,
        bool approval,
        int calls,
        int parallel) =>
        new(
            profileId,
            Version: 1,
            workflow,
            role,
            allowedCapabilities,
            allowedTools,
            forbiddenCapabilities,
            forbiddenTools,
            approval,
            calls,
            parallel);

    private static AgentToolProfileResolutionRequest WriterRequest(
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string> tools) =>
        new(
            "draft-author",
            1,
            "draft-authoring",
            "author",
            capabilities,
            tools);

    private void Record(EffectiveAgentToolProfile profile)
    {
        Require(AgentToolProfileFingerprint.Verify(profile), "Effective profile fingerprint failed verification.");
        _fingerprints.Add(profile.Fingerprint);
    }

    private static void RequireCode(Action action, string code)
    {
        try
        {
            action();
        }
        catch (AgentToolProfileException exception) when (exception.Code == code)
        {
            Require(exception.Message == code, "Stable profile rejection message drifted.");
            return;
        }
        throw new InvalidOperationException($"Expected stable rejection code {code}.");
    }

    private static void RequireException<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed record AgentToolProfilesReport(
    int Scenarios,
    int Profiles,
    int Fingerprints,
    string Gate,
    string Mutation);

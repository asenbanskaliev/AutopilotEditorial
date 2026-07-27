namespace BookStudio.Application.OpenCode;

public static class AgentToolProfileCapabilities
{
    public const string ArtifactRead = "artifact.read";
    public const string DraftWrite = "draft.write";
    public const string DraftValidate = "draft.validate";
    public const string QualityAudit = "quality.audit";
    public const string QualityGate = "quality.gate";
    public const string ReleasePrepare = "release.prepare";
    public const string ReleasePreflight = "release.preflight";
    public const string OperationsRead = "operations.read";

    public static IReadOnlySet<string> Known { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ArtifactRead,
            DraftWrite,
            DraftValidate,
            QualityAudit,
            QualityGate,
            ReleasePrepare,
            ReleasePreflight,
            OperationsRead,
        };
}

public static class AgentToolProfileTools
{
    public const string ArtifactGet = "book.artifact.get";
    public const string ArtifactCompare = "book.artifact.compare";
    public const string DraftRegister = "book.draft.register";
    public const string DraftValidate = "book.draft.validate";
    public const string AuditRun = "book.audit.run";
    public const string GateEvaluate = "book.gate.evaluate";
    public const string ReleasePrepare = "book.release.prepare";
    public const string PreflightRun = "book.preflight.run";
    public const string OpsStatus = "book.ops.status";
    public const string OpsDiagnostics = "book.ops.diagnostics";

    public static IReadOnlySet<string> Known { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ArtifactGet,
            ArtifactCompare,
            DraftRegister,
            DraftValidate,
            AuditRun,
            GateEvaluate,
            ReleasePrepare,
            PreflightRun,
            OpsStatus,
            OpsDiagnostics,
        };
}

public static class AgentToolProfileErrorCodes
{
    public const string Invalid = "agent_profile_invalid";
    public const string ProfileNotFound = "agent_profile_not_found";
    public const string ProfileVersionNotFound = "agent_profile_version_not_found";
    public const string WorkflowMismatch = "agent_profile_workflow_mismatch";
    public const string RoleMismatch = "agent_profile_role_mismatch";
    public const string UnknownCapability = "agent_profile_unknown_capability";
    public const string UnknownTool = "agent_profile_unknown_tool";
    public const string PermissionDenied = "agent_profile_permission_denied";
    public const string PrivilegeEscalation = "agent_profile_privilege_escalation";
    public const string ProviderUnsupported = "agent_profile_provider_unsupported";
    public const string LimitsInvalid = "agent_profile_limits_invalid";
}

public sealed class AgentToolProfileException : Exception
{
    public AgentToolProfileException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record AgentToolProfileDefinition(
    string ProfileId,
    int Version,
    string Workflow,
    string Role,
    IReadOnlyList<string> AllowedCapabilities,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> ForbiddenCapabilities,
    IReadOnlyList<string> ForbiddenTools,
    bool RequiresHumanApproval,
    int MaximumToolCalls,
    int MaximumParallelTools);

public sealed record AgentToolProfileResolutionRequest(
    string ProfileId,
    int Version,
    string Workflow,
    string Role,
    IReadOnlyList<string> RequestedCapabilities,
    IReadOnlyList<string> RequestedTools,
    EffectiveAgentToolProfile? Parent = null);

public sealed record AgentToolProfileProductLimits(
    int MaximumToolCalls,
    int MaximumParallelTools)
{
    public static AgentToolProfileProductLimits Default { get; } = new(64, 8);

    public void Validate()
    {
        if (MaximumToolCalls is < 1 or > 100_000 ||
            MaximumParallelTools is < 1 or > 1024 ||
            MaximumParallelTools > MaximumToolCalls)
        {
            throw new AgentToolProfileException(AgentToolProfileErrorCodes.LimitsInvalid);
        }
    }
}

public sealed class EffectiveAgentToolProfile : IEquatable<EffectiveAgentToolProfile>
{
    public EffectiveAgentToolProfile(
        int catalogVersion,
        string profileId,
        int profileVersion,
        string workflow,
        string role,
        IReadOnlyList<string> allowedCapabilities,
        IReadOnlyList<string> allowedTools,
        bool requiresHumanApproval,
        int maximumToolCalls,
        int maximumParallelTools,
        string? parentFingerprint,
        string fingerprint)
    {
        CatalogVersion = catalogVersion;
        ProfileId = profileId;
        ProfileVersion = profileVersion;
        Workflow = workflow;
        Role = role;
        AllowedCapabilities = allowedCapabilities;
        AllowedTools = allowedTools;
        RequiresHumanApproval = requiresHumanApproval;
        MaximumToolCalls = maximumToolCalls;
        MaximumParallelTools = maximumParallelTools;
        ParentFingerprint = parentFingerprint;
        Fingerprint = fingerprint;
    }

    public int CatalogVersion { get; }
    public string ProfileId { get; }
    public int ProfileVersion { get; }
    public string Workflow { get; }
    public string Role { get; }
    public IReadOnlyList<string> AllowedCapabilities { get; }
    public IReadOnlyList<string> AllowedTools { get; }
    public bool RequiresHumanApproval { get; }
    public int MaximumToolCalls { get; }
    public int MaximumParallelTools { get; }
    public string? ParentFingerprint { get; }
    public string Fingerprint { get; }

    internal EffectiveAgentToolProfile WithFingerprint(string fingerprint) =>
        new(
            CatalogVersion,
            ProfileId,
            ProfileVersion,
            Workflow,
            Role,
            AllowedCapabilities,
            AllowedTools,
            RequiresHumanApproval,
            MaximumToolCalls,
            MaximumParallelTools,
            ParentFingerprint,
            fingerprint);

    public bool Equals(EffectiveAgentToolProfile? other) =>
        other is not null &&
        CatalogVersion == other.CatalogVersion &&
        ProfileVersion == other.ProfileVersion &&
        RequiresHumanApproval == other.RequiresHumanApproval &&
        MaximumToolCalls == other.MaximumToolCalls &&
        MaximumParallelTools == other.MaximumParallelTools &&
        string.Equals(ProfileId, other.ProfileId, StringComparison.Ordinal) &&
        string.Equals(Workflow, other.Workflow, StringComparison.Ordinal) &&
        string.Equals(Role, other.Role, StringComparison.Ordinal) &&
        string.Equals(ParentFingerprint, other.ParentFingerprint, StringComparison.Ordinal) &&
        string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal) &&
        AllowedCapabilities.SequenceEqual(other.AllowedCapabilities, StringComparer.Ordinal) &&
        AllowedTools.SequenceEqual(other.AllowedTools, StringComparer.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as EffectiveAgentToolProfile);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Fingerprint);
}

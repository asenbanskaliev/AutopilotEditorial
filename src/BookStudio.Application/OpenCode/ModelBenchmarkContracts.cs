namespace BookStudio.Application.OpenCode;

public static class ModelBenchmarkDimensions
{
    public const string LongFormCoherence = "long_form_coherence";
    public const string InstructionFollowing = "instruction_following";
    public const string StructuredOutput = "structured_output";
    public const string EditingAccuracy = "editing_accuracy";
    public const string ReasoningQuality = "reasoning_quality";
    public const string Factuality = "factuality";
    public const string MultilingualQuality = "multilingual_quality";
    public const string LatencyEfficiency = "latency_efficiency";
    public const string CostEfficiency = "cost_efficiency";

    public static IReadOnlySet<string> Known { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            LongFormCoherence,
            InstructionFollowing,
            StructuredOutput,
            EditingAccuracy,
            ReasoningQuality,
            Factuality,
            MultilingualQuality,
            LatencyEfficiency,
            CostEfficiency,
        };
}

public static class ModelLocalities
{
    public const string Local = "local";
    public const string PrivateRemote = "private_remote";
    public const string PublicRemote = "public_remote";

    public static IReadOnlySet<string> Known { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Local,
            PrivateRemote,
            PublicRemote,
        };
}

public static class ModelSelectionModes
{
    public const string Ranked = "ranked";
    public const string Fallback = "fallback";
}

public static class ModelAssignmentReasonCodes
{
    public const string SelectedByRank = "model_selected_by_rank";
    public const string SelectedByFallback = "model_selected_by_fallback";
    public const string PreferredLocalityMatched = "model_preferred_locality_matched";
}

public static class ModelAssignmentErrorCodes
{
    public const string Invalid = "model_benchmark_invalid";
    public const string CatalogNotFound = "model_benchmark_catalog_not_found";
    public const string RolePolicyNotFound = "model_role_policy_not_found";
    public const string RolePolicyVersionNotFound = "model_role_policy_version_not_found";
    public const string MissingEvidence = "model_benchmark_missing_evidence";
    public const string StaleEvidence = "model_benchmark_stale_evidence";
    public const string LowConfidence = "model_benchmark_low_confidence";
    public const string NoEligibleModel = "model_assignment_no_eligible_model";
    public const string ProviderUnavailable = "model_assignment_provider_unavailable";
    public const string ProfileFingerprintInvalid = "model_assignment_profile_fingerprint_invalid";
    public const string ProviderUnsupported = "model_assignment_provider_unsupported";
    public const string LimitsInvalid = "model_assignment_limits_invalid";
}

public sealed class ModelAssignmentException : Exception
{
    public ModelAssignmentException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record ModelBenchmarkEvidence(
    string Dimension,
    int ScoreBasisPoints,
    int SampleCount,
    int ConfidenceBasisPoints,
    long MeasuredAtEpochSeconds,
    string SourceId,
    string SourceDigestSha256);

public sealed record ModelBenchmarkDefinition(
    string ModelId,
    int Revision,
    string ProviderFamily,
    string ProviderModelKey,
    string Locality,
    int ContextWindowTokens,
    int MaximumOutputTokens,
    long InputCostMicrosPerMillion,
    long OutputCostMicrosPerMillion,
    int MedianLatencyMs,
    bool SupportsStructuredOutput,
    bool SupportsToolCalling,
    bool SupportsVision,
    bool SupportsReasoning,
    int SafetyTier,
    IReadOnlyList<ModelBenchmarkEvidence> BenchmarkEvidence);

public sealed record ModelRolePolicyDefinition(
    string RoleId,
    int Version,
    IReadOnlyList<string> PrimaryModelIds,
    IReadOnlyList<string> FallbackModelIds,
    IReadOnlyList<string> RequiredDimensions,
    long MaximumEvidenceAgeSeconds,
    int MinimumConfidenceBasisPoints,
    int MinimumContextWindowTokens,
    int MinimumOutputTokens,
    long MaximumInputCostMicrosPerMillion,
    long MaximumOutputCostMicrosPerMillion,
    int MaximumMedianLatencyMs,
    int MinimumSafetyTier,
    IReadOnlyList<string> AllowedLocalities,
    bool RequiresStructuredOutput,
    bool RequiresToolCalling,
    bool RequiresVision,
    bool RequiresReasoning,
    IReadOnlyDictionary<string, int> WeightsBasisPoints);

public sealed record ModelProviderAvailability(
    string ModelId,
    int Revision,
    string ProviderFamily,
    string ProviderModelKey);

public sealed record ModelAssignmentRequest(
    string RoleId,
    int RolePolicyVersion,
    long EvaluationEpochSeconds,
    string RequiredProfileFingerprint,
    IReadOnlyList<ModelProviderAvailability> AvailableProviderModels,
    string? PreferredLocality = null);

public sealed class ModelAssignmentDecision : IEquatable<ModelAssignmentDecision>
{
    internal ModelAssignmentDecision(
        int catalogVersion,
        string roleId,
        int rolePolicyVersion,
        string selectedModelId,
        int selectedRevision,
        string providerFamily,
        string providerModelKey,
        string selectionMode,
        int weightedScoreBasisPoints,
        string profileFingerprint,
        string assignmentFingerprint,
        IReadOnlyList<string> reasonCodes,
        int eligibleCandidateCount)
    {
        CatalogVersion = catalogVersion;
        RoleId = roleId;
        RolePolicyVersion = rolePolicyVersion;
        SelectedModelId = selectedModelId;
        SelectedRevision = selectedRevision;
        ProviderFamily = providerFamily;
        ProviderModelKey = providerModelKey;
        SelectionMode = selectionMode;
        WeightedScoreBasisPoints = weightedScoreBasisPoints;
        ProfileFingerprint = profileFingerprint;
        AssignmentFingerprint = assignmentFingerprint;
        ReasonCodes = reasonCodes;
        EligibleCandidateCount = eligibleCandidateCount;
    }

    public int CatalogVersion { get; }
    public string RoleId { get; }
    public int RolePolicyVersion { get; }
    public string SelectedModelId { get; }
    public int SelectedRevision { get; }
    public string ProviderFamily { get; }
    public string ProviderModelKey { get; }
    public string SelectionMode { get; }
    public int WeightedScoreBasisPoints { get; }
    public string ProfileFingerprint { get; }
    public string AssignmentFingerprint { get; }
    public IReadOnlyList<string> ReasonCodes { get; }
    public int EligibleCandidateCount { get; }

    internal ModelAssignmentDecision WithFingerprint(string fingerprint) =>
        new(
            CatalogVersion,
            RoleId,
            RolePolicyVersion,
            SelectedModelId,
            SelectedRevision,
            ProviderFamily,
            ProviderModelKey,
            SelectionMode,
            WeightedScoreBasisPoints,
            ProfileFingerprint,
            fingerprint,
            ReasonCodes,
            EligibleCandidateCount);

    public bool Equals(ModelAssignmentDecision? other) =>
        other is not null &&
        CatalogVersion == other.CatalogVersion &&
        RolePolicyVersion == other.RolePolicyVersion &&
        SelectedRevision == other.SelectedRevision &&
        WeightedScoreBasisPoints == other.WeightedScoreBasisPoints &&
        EligibleCandidateCount == other.EligibleCandidateCount &&
        string.Equals(RoleId, other.RoleId, StringComparison.Ordinal) &&
        string.Equals(SelectedModelId, other.SelectedModelId, StringComparison.Ordinal) &&
        string.Equals(ProviderFamily, other.ProviderFamily, StringComparison.Ordinal) &&
        string.Equals(ProviderModelKey, other.ProviderModelKey, StringComparison.Ordinal) &&
        string.Equals(SelectionMode, other.SelectionMode, StringComparison.Ordinal) &&
        string.Equals(ProfileFingerprint, other.ProfileFingerprint, StringComparison.Ordinal) &&
        string.Equals(AssignmentFingerprint, other.AssignmentFingerprint, StringComparison.Ordinal) &&
        ReasonCodes.SequenceEqual(other.ReasonCodes, StringComparer.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as ModelAssignmentDecision);

    public static bool operator ==(
        ModelAssignmentDecision? left,
        ModelAssignmentDecision? right) =>
        ReferenceEquals(left, right) || (left?.Equals(right) ?? false);

    public static bool operator !=(
        ModelAssignmentDecision? left,
        ModelAssignmentDecision? right) =>
        !(left == right);

    public override int GetHashCode() =>
        StringComparer.Ordinal.GetHashCode(AssignmentFingerprint);
}

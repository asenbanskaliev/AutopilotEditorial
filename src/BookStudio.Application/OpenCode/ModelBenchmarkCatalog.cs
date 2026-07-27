using System.Collections.ObjectModel;
using System.Text;

namespace BookStudio.Application.OpenCode;

public sealed class ModelBenchmarkCatalog
{
    public const int MaximumModels = 256;
    public const int MaximumRolePolicies = 256;
    public const int MaximumEvidenceEntries = 64;
    public const int MaximumListEntries = 256;
    public const int MaximumIdentifierBytes = 192;
    public const int MaximumContextTokens = 10_000_000;
    public const int MaximumOutputTokens = 1_000_000;
    public const long MaximumCostMicrosPerMillion = 1_000_000_000_000;
    public const int MaximumLatencyMs = 86_400_000;
    public const long MaximumEvidenceAgeSeconds = 315_576_000;

    private readonly Dictionary<string, SortedDictionary<int, ModelBenchmarkDefinition>> _modelsById =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedDictionary<int, ModelRolePolicyDefinition>> _rolesById =
        new(StringComparer.Ordinal);

    public ModelBenchmarkCatalog(
        int catalogVersion,
        long measuredAtEpochSeconds,
        IReadOnlyList<ModelBenchmarkDefinition> models,
        IReadOnlyList<ModelRolePolicyDefinition> rolePolicies)
    {
        if (catalogVersion < 1 ||
            measuredAtEpochSeconds < 0 ||
            models is null ||
            rolePolicies is null ||
            models.Count is < 1 or > MaximumModels ||
            rolePolicies.Count is < 1 or > MaximumRolePolicies)
        {
            throw Invalid();
        }

        CatalogVersion = catalogVersion;
        MeasuredAtEpochSeconds = measuredAtEpochSeconds;

        var normalizedModels = new List<ModelBenchmarkDefinition>(models.Count);
        var providerReferences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in models)
        {
            var model = NormalizeModel(source, measuredAtEpochSeconds);
            if (!_modelsById.TryGetValue(model.ModelId, out var revisions))
            {
                revisions = new SortedDictionary<int, ModelBenchmarkDefinition>();
                _modelsById.Add(model.ModelId, revisions);
            }
            if (!revisions.TryAdd(model.Revision, model))
            {
                throw Invalid();
            }

            var providerReference = model.ProviderFamily + "\0" + model.ProviderModelKey;
            if (!providerReferences.Add(providerReference))
            {
                throw Invalid();
            }
            normalizedModels.Add(model);
        }

        var normalizedPolicies = new List<ModelRolePolicyDefinition>(rolePolicies.Count);
        foreach (var source in rolePolicies)
        {
            var policy = NormalizePolicy(source);
            ValidatePolicyModelReferences(policy);
            if (!_rolesById.TryGetValue(policy.RoleId, out var versions))
            {
                versions = new SortedDictionary<int, ModelRolePolicyDefinition>();
                _rolesById.Add(policy.RoleId, versions);
            }
            if (!versions.TryAdd(policy.Version, policy))
            {
                throw Invalid();
            }
            normalizedPolicies.Add(policy);
        }

        Models = Array.AsReadOnly(normalizedModels
            .OrderBy(item => item.ModelId, StringComparer.Ordinal)
            .ThenBy(item => item.Revision)
            .ToArray());
        RolePolicies = Array.AsReadOnly(normalizedPolicies
            .OrderBy(item => item.RoleId, StringComparer.Ordinal)
            .ThenBy(item => item.Version)
            .ToArray());
    }

    public int CatalogVersion { get; }
    public long MeasuredAtEpochSeconds { get; }
    public IReadOnlyList<ModelBenchmarkDefinition> Models { get; }
    public IReadOnlyList<ModelRolePolicyDefinition> RolePolicies { get; }

    internal bool ContainsRoleId(string roleId) => _rolesById.ContainsKey(roleId);

    internal bool TryGetRolePolicy(
        string roleId,
        int version,
        out ModelRolePolicyDefinition? policy)
    {
        if (_rolesById.TryGetValue(roleId, out var versions) &&
            versions.TryGetValue(version, out var found))
        {
            policy = found;
            return true;
        }
        policy = null;
        return false;
    }

    internal IReadOnlyList<ModelBenchmarkDefinition> GetModels(string modelId)
    {
        if (!_modelsById.TryGetValue(modelId, out var revisions))
        {
            return Array.Empty<ModelBenchmarkDefinition>();
        }
        return Array.AsReadOnly(revisions.Values.ToArray());
    }

    internal bool TryGetModel(
        string modelId,
        int revision,
        out ModelBenchmarkDefinition? model)
    {
        if (_modelsById.TryGetValue(modelId, out var revisions) &&
            revisions.TryGetValue(revision, out var found))
        {
            model = found;
            return true;
        }
        model = null;
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
                  character is '.' or '_' or '-' or '/')))
        {
            throw Invalid();
        }
        return value;
    }

    internal static bool IsLowerHexSha256(string value) =>
        value is not null &&
        value.Length == 64 &&
        value.All(character =>
            (character is >= '0' and <= '9') ||
            (character is >= 'a' and <= 'f'));

    internal static IReadOnlyList<string> NormalizeIdentifierList(
        IReadOnlyList<string> values,
        IReadOnlySet<string>? known = null,
        bool allowEmpty = true)
    {
        if (values is null ||
            values.Count > MaximumListEntries ||
            (!allowEmpty && values.Count == 0))
        {
            throw Invalid();
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in values)
        {
            var value = ValidateIdentifier(source);
            if (!unique.Add(value) || (known is not null && !known.Contains(value)))
            {
                throw Invalid();
            }
        }
        return Array.AsReadOnly(unique.Order(StringComparer.Ordinal).ToArray());
    }

    private static ModelBenchmarkDefinition NormalizeModel(
        ModelBenchmarkDefinition source,
        long catalogMeasuredAt)
    {
        if (source is null ||
            source.Revision < 1 ||
            source.ContextWindowTokens is < 1 or > MaximumContextTokens ||
            source.MaximumOutputTokens is < 1 or > MaximumOutputTokens ||
            source.MaximumOutputTokens > source.ContextWindowTokens ||
            source.InputCostMicrosPerMillion is < 0 or > MaximumCostMicrosPerMillion ||
            source.OutputCostMicrosPerMillion is < 0 or > MaximumCostMicrosPerMillion ||
            source.MedianLatencyMs is < 1 or > MaximumLatencyMs ||
            source.SafetyTier is < 1 or > 5 ||
            source.BenchmarkEvidence is null ||
            source.BenchmarkEvidence.Count is < 1 or > MaximumEvidenceEntries ||
            !ModelLocalities.Known.Contains(source.Locality))
        {
            throw Invalid();
        }

        var evidence = new List<ModelBenchmarkEvidence>(source.BenchmarkEvidence.Count);
        var dimensions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in source.BenchmarkEvidence)
        {
            if (item is null ||
                !ModelBenchmarkDimensions.Known.Contains(item.Dimension) ||
                !dimensions.Add(item.Dimension) ||
                item.ScoreBasisPoints is < 0 or > 10_000 ||
                item.SampleCount < 1 ||
                item.ConfidenceBasisPoints is < 0 or > 10_000 ||
                item.MeasuredAtEpochSeconds < 0 ||
                item.MeasuredAtEpochSeconds > catalogMeasuredAt ||
                !IsLowerHexSha256(item.SourceDigestSha256))
            {
                throw Invalid();
            }

            evidence.Add(new ModelBenchmarkEvidence(
                item.Dimension,
                item.ScoreBasisPoints,
                item.SampleCount,
                item.ConfidenceBasisPoints,
                item.MeasuredAtEpochSeconds,
                ValidateIdentifier(item.SourceId),
                item.SourceDigestSha256));
        }

        return new ModelBenchmarkDefinition(
            ValidateIdentifier(source.ModelId),
            source.Revision,
            ValidateIdentifier(source.ProviderFamily),
            ValidateIdentifier(source.ProviderModelKey),
            source.Locality,
            source.ContextWindowTokens,
            source.MaximumOutputTokens,
            source.InputCostMicrosPerMillion,
            source.OutputCostMicrosPerMillion,
            source.MedianLatencyMs,
            source.SupportsStructuredOutput,
            source.SupportsToolCalling,
            source.SupportsVision,
            source.SupportsReasoning,
            source.SafetyTier,
            Array.AsReadOnly(evidence
                .OrderBy(item => item.Dimension, StringComparer.Ordinal)
                .ToArray()));
    }

    private static ModelRolePolicyDefinition NormalizePolicy(ModelRolePolicyDefinition source)
    {
        if (source is null ||
            source.Version < 1 ||
            source.MaximumEvidenceAgeSeconds is < 0 or > MaximumEvidenceAgeSeconds ||
            source.MinimumConfidenceBasisPoints is < 0 or > 10_000 ||
            source.MinimumContextWindowTokens is < 1 or > MaximumContextTokens ||
            source.MinimumOutputTokens is < 1 or > MaximumOutputTokens ||
            source.MinimumOutputTokens > source.MinimumContextWindowTokens ||
            source.MaximumInputCostMicrosPerMillion is < 0 or > MaximumCostMicrosPerMillion ||
            source.MaximumOutputCostMicrosPerMillion is < 0 or > MaximumCostMicrosPerMillion ||
            source.MaximumMedianLatencyMs is < 1 or > MaximumLatencyMs ||
            source.MinimumSafetyTier is < 1 or > 5 ||
            source.WeightsBasisPoints is null ||
            source.WeightsBasisPoints.Count is < 1 or > MaximumListEntries)
        {
            throw Invalid();
        }

        var primary = NormalizeIdentifierList(source.PrimaryModelIds);
        var fallback = NormalizeIdentifierList(source.FallbackModelIds);
        if (primary.Count == 0 && fallback.Count == 0 ||
            primary.Intersect(fallback, StringComparer.Ordinal).Any())
        {
            throw Invalid();
        }

        var required = NormalizeIdentifierList(
            source.RequiredDimensions,
            ModelBenchmarkDimensions.Known,
            allowEmpty: false);
        var localities = NormalizeIdentifierList(
            source.AllowedLocalities,
            ModelLocalities.Known,
            allowEmpty: false);

        var weights = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var weightTotal = 0;
        foreach (var pair in source.WeightsBasisPoints)
        {
            var dimension = ValidateIdentifier(pair.Key);
            if (!ModelBenchmarkDimensions.Known.Contains(dimension) ||
                pair.Value is < 1 or > 10_000 ||
                !weights.TryAdd(dimension, pair.Value))
            {
                throw Invalid();
            }
            weightTotal = checked(weightTotal + pair.Value);
        }
        if (weightTotal != 10_000)
        {
            throw Invalid();
        }

        return new ModelRolePolicyDefinition(
            ValidateIdentifier(source.RoleId),
            source.Version,
            primary,
            fallback,
            required,
            source.MaximumEvidenceAgeSeconds,
            source.MinimumConfidenceBasisPoints,
            source.MinimumContextWindowTokens,
            source.MinimumOutputTokens,
            source.MaximumInputCostMicrosPerMillion,
            source.MaximumOutputCostMicrosPerMillion,
            source.MaximumMedianLatencyMs,
            source.MinimumSafetyTier,
            localities,
            source.RequiresStructuredOutput,
            source.RequiresToolCalling,
            source.RequiresVision,
            source.RequiresReasoning,
            new ReadOnlyDictionary<string, int>(weights));
    }

    private void ValidatePolicyModelReferences(ModelRolePolicyDefinition policy)
    {
        foreach (var modelId in policy.PrimaryModelIds.Concat(policy.FallbackModelIds))
        {
            if (!_modelsById.ContainsKey(modelId))
            {
                throw Invalid();
            }
        }
    }

    private static ModelAssignmentException Invalid() =>
        new(ModelAssignmentErrorCodes.Invalid);
}

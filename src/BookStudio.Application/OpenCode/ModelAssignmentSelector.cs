using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Application.OpenCode;

public sealed class ModelAssignmentSelector : IModelAssignmentSelector
{
    private readonly ModelBenchmarkCatalog _catalog;

    public ModelAssignmentSelector(ModelBenchmarkCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public ModelAssignmentDecision Select(
        ModelAssignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var roleId = ValidateRequestIdentifier(request.RoleId);
        if (request.RolePolicyVersion < 1 || request.EvaluationEpochSeconds < 0)
        {
            throw Invalid();
        }
        if (!ModelBenchmarkCatalog.IsLowerHexSha256(request.RequiredProfileFingerprint))
        {
            throw new ModelAssignmentException(ModelAssignmentErrorCodes.ProfileFingerprintInvalid);
        }
        if (request.PreferredLocality is not null &&
            !ModelLocalities.Known.Contains(request.PreferredLocality))
        {
            throw Invalid();
        }

        var availability = NormalizeAvailability(request.AvailableProviderModels);
        if (!_catalog.ContainsRoleId(roleId))
        {
            throw new ModelAssignmentException(ModelAssignmentErrorCodes.RolePolicyNotFound);
        }
        if (!_catalog.TryGetRolePolicy(roleId, request.RolePolicyVersion, out var policy) ||
            policy is null)
        {
            throw new ModelAssignmentException(ModelAssignmentErrorCodes.RolePolicyVersionNotFound);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var primaryEvaluation = EvaluateModelIds(
            policy.PrimaryModelIds,
            policy,
            request,
            availability,
            cancellationToken);
        if (primaryEvaluation.Eligible.Count > 0)
        {
            var selected = Rank(primaryEvaluation.Eligible, request.PreferredLocality).First();
            return CreateDecision(
                policy,
                selected,
                ModelSelectionModes.Ranked,
                primaryEvaluation.Eligible.Count,
                request.RequiredProfileFingerprint,
                request.PreferredLocality);
        }

        var fallbackEvaluation = EvaluateModelIds(
            policy.FallbackModelIds,
            policy,
            request,
            availability,
            cancellationToken);
        foreach (var fallbackId in policy.FallbackModelIds)
        {
            var candidates = fallbackEvaluation.Eligible
                .Where(item => string.Equals(item.Model.ModelId, fallbackId, StringComparison.Ordinal))
                .ToArray();
            if (candidates.Length == 0)
            {
                continue;
            }

            var selected = Rank(candidates, request.PreferredLocality).First();
            return CreateDecision(
                policy,
                selected,
                ModelSelectionModes.Fallback,
                fallbackEvaluation.Eligible.Count,
                request.RequiredProfileFingerprint,
                request.PreferredLocality);
        }

        ThrowNoSelection(primaryEvaluation.Merge(fallbackEvaluation));
        throw new InvalidOperationException("Unreachable model selection state.");
    }

    private CandidateEvaluation EvaluateModelIds(
        IReadOnlyList<string> modelIds,
        ModelRolePolicyDefinition policy,
        ModelAssignmentRequest request,
        IReadOnlyDictionary<ModelAvailabilityKey, ModelProviderAvailability> availability,
        CancellationToken cancellationToken)
    {
        var result = new CandidateEvaluation();
        foreach (var modelId in modelIds)
        {
            foreach (var model in _catalog.GetModels(modelId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = new ModelAvailabilityKey(model.ModelId, model.Revision);
                if (!availability.TryGetValue(key, out var advertised) ||
                    !string.Equals(advertised.ProviderFamily, model.ProviderFamily, StringComparison.Ordinal) ||
                    !string.Equals(advertised.ProviderModelKey, model.ProviderModelKey, StringComparison.Ordinal))
                {
                    result.Unavailable++;
                    continue;
                }

                result.Advertised++;
                var eligibility = EvaluateEligibility(model, policy, request.EvaluationEpochSeconds);
                if (!eligibility.IsEligible)
                {
                    result.RecordFailure(eligibility.FailureKind);
                    continue;
                }

                var score = CalculateWeightedScore(model, policy);
                result.Eligible.Add(new RankedCandidate(model, score));
            }
        }
        return result;
    }

    private static EligibilityResult EvaluateEligibility(
        ModelBenchmarkDefinition model,
        ModelRolePolicyDefinition policy,
        long evaluationEpochSeconds)
    {
        var evidenceByDimension = model.BenchmarkEvidence.ToDictionary(
            item => item.Dimension,
            StringComparer.Ordinal);
        var requiredEvidence = policy.RequiredDimensions
            .Concat(policy.WeightsBasisPoints.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var dimension in requiredEvidence)
        {
            if (!evidenceByDimension.TryGetValue(dimension, out var evidence))
            {
                return EligibilityResult.Failed(EligibilityFailureKind.MissingEvidence);
            }
            if (evidence.MeasuredAtEpochSeconds > evaluationEpochSeconds ||
                evaluationEpochSeconds - evidence.MeasuredAtEpochSeconds > policy.MaximumEvidenceAgeSeconds)
            {
                return EligibilityResult.Failed(EligibilityFailureKind.StaleEvidence);
            }
            if (evidence.ConfidenceBasisPoints < policy.MinimumConfidenceBasisPoints)
            {
                return EligibilityResult.Failed(EligibilityFailureKind.LowConfidence);
            }
        }

        if (model.ContextWindowTokens < policy.MinimumContextWindowTokens ||
            model.MaximumOutputTokens < policy.MinimumOutputTokens ||
            model.InputCostMicrosPerMillion > policy.MaximumInputCostMicrosPerMillion ||
            model.OutputCostMicrosPerMillion > policy.MaximumOutputCostMicrosPerMillion ||
            model.MedianLatencyMs > policy.MaximumMedianLatencyMs ||
            model.SafetyTier < policy.MinimumSafetyTier ||
            !policy.AllowedLocalities.Contains(model.Locality, StringComparer.Ordinal) ||
            (policy.RequiresStructuredOutput && !model.SupportsStructuredOutput) ||
            (policy.RequiresToolCalling && !model.SupportsToolCalling) ||
            (policy.RequiresVision && !model.SupportsVision) ||
            (policy.RequiresReasoning && !model.SupportsReasoning))
        {
            return EligibilityResult.Failed(EligibilityFailureKind.HardConstraint);
        }

        return EligibilityResult.Eligible();
    }

    private static int CalculateWeightedScore(
        ModelBenchmarkDefinition model,
        ModelRolePolicyDefinition policy)
    {
        var evidenceByDimension = model.BenchmarkEvidence.ToDictionary(
            item => item.Dimension,
            StringComparer.Ordinal);
        long total = 0;
        checked
        {
            foreach (var pair in policy.WeightsBasisPoints)
            {
                total += (long)evidenceByDimension[pair.Key].ScoreBasisPoints * pair.Value;
            }
        }
        return Math.Min(10_000, checked((int)(total / 10_000)));
    }

    private static IEnumerable<RankedCandidate> Rank(
        IReadOnlyList<RankedCandidate> candidates,
        string? preferredLocality) =>
        candidates.OrderBy(item => item, new RankedCandidateComparer(preferredLocality));

    private ModelAssignmentDecision CreateDecision(
        ModelRolePolicyDefinition policy,
        RankedCandidate selected,
        string selectionMode,
        int eligibleCandidateCount,
        string profileFingerprint,
        string? preferredLocality)
    {
        var reasons = new List<string>
        {
            selectionMode == ModelSelectionModes.Ranked
                ? ModelAssignmentReasonCodes.SelectedByRank
                : ModelAssignmentReasonCodes.SelectedByFallback,
        };
        if (preferredLocality is not null &&
            string.Equals(selected.Model.Locality, preferredLocality, StringComparison.Ordinal))
        {
            reasons.Add(ModelAssignmentReasonCodes.PreferredLocalityMatched);
        }

        var unsigned = new ModelAssignmentDecision(
            _catalog.CatalogVersion,
            policy.RoleId,
            policy.Version,
            selected.Model.ModelId,
            selected.Model.Revision,
            selected.Model.ProviderFamily,
            selected.Model.ProviderModelKey,
            selectionMode,
            selected.WeightedScoreBasisPoints,
            profileFingerprint,
            string.Empty,
            Array.AsReadOnly(reasons.Order(StringComparer.Ordinal).ToArray()),
            eligibleCandidateCount);
        return unsigned.WithFingerprint(ModelAssignmentFingerprint.Compute(unsigned));
    }

    private static IReadOnlyDictionary<ModelAvailabilityKey, ModelProviderAvailability> NormalizeAvailability(
        IReadOnlyList<ModelProviderAvailability> values)
    {
        if (values is null || values.Count > ModelBenchmarkCatalog.MaximumModels)
        {
            throw Invalid();
        }

        var result = new Dictionary<ModelAvailabilityKey, ModelProviderAvailability>();
        foreach (var source in values)
        {
            if (source is null || source.Revision < 1)
            {
                throw Invalid();
            }
            var normalized = new ModelProviderAvailability(
                ValidateRequestIdentifier(source.ModelId),
                source.Revision,
                ValidateRequestIdentifier(source.ProviderFamily),
                ValidateRequestIdentifier(source.ProviderModelKey));
            if (!result.TryAdd(
                    new ModelAvailabilityKey(normalized.ModelId, normalized.Revision),
                    normalized))
            {
                throw Invalid();
            }
        }
        return result;
    }

    private static string ValidateRequestIdentifier(string value)
    {
        try
        {
            return ModelBenchmarkCatalog.ValidateIdentifier(value);
        }
        catch (ModelAssignmentException)
        {
            throw Invalid();
        }
    }

    private static void ThrowNoSelection(CandidateEvaluation evaluation)
    {
        if (evaluation.Advertised == 0)
        {
            throw new ModelAssignmentException(ModelAssignmentErrorCodes.ProviderUnavailable);
        }
        if (evaluation.MissingEvidence == evaluation.Advertised)
        {
            throw new ModelAssignmentException(ModelAssignmentErrorCodes.MissingEvidence);
        }
        if (evaluation.StaleEvidence == evaluation.Advertised)
        {
            throw new ModelAssignmentException(ModelAssignmentErrorCodes.StaleEvidence);
        }
        if (evaluation.LowConfidence == evaluation.Advertised)
        {
            throw new ModelAssignmentException(ModelAssignmentErrorCodes.LowConfidence);
        }
        throw new ModelAssignmentException(ModelAssignmentErrorCodes.NoEligibleModel);
    }

    private static ModelAssignmentException Invalid() =>
        new(ModelAssignmentErrorCodes.Invalid);

    private sealed record RankedCandidate(
        ModelBenchmarkDefinition Model,
        int WeightedScoreBasisPoints);

    private sealed class RankedCandidateComparer : IComparer<RankedCandidate>
    {
        private readonly string? _preferredLocality;

        public RankedCandidateComparer(string? preferredLocality)
        {
            _preferredLocality = preferredLocality;
        }

        public int Compare(RankedCandidate? left, RankedCandidate? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }
            if (left is null)
            {
                return 1;
            }
            if (right is null)
            {
                return -1;
            }

            var result = right.WeightedScoreBasisPoints.CompareTo(left.WeightedScoreBasisPoints);
            if (result != 0)
            {
                return result;
            }

            var leftPreferred = _preferredLocality is not null &&
                string.Equals(left.Model.Locality, _preferredLocality, StringComparison.Ordinal);
            var rightPreferred = _preferredLocality is not null &&
                string.Equals(right.Model.Locality, _preferredLocality, StringComparison.Ordinal);
            result = rightPreferred.CompareTo(leftPreferred);
            if (result != 0)
            {
                return result;
            }

            var leftCost = checked(left.Model.InputCostMicrosPerMillion + left.Model.OutputCostMicrosPerMillion);
            var rightCost = checked(right.Model.InputCostMicrosPerMillion + right.Model.OutputCostMicrosPerMillion);
            result = leftCost.CompareTo(rightCost);
            if (result != 0)
            {
                return result;
            }

            result = left.Model.MedianLatencyMs.CompareTo(right.Model.MedianLatencyMs);
            if (result != 0)
            {
                return result;
            }

            result = StringComparer.Ordinal.Compare(left.Model.ModelId, right.Model.ModelId);
            if (result != 0)
            {
                return result;
            }
            return right.Model.Revision.CompareTo(left.Model.Revision);
        }
    }

    private readonly record struct ModelAvailabilityKey(string ModelId, int Revision);

    private enum EligibilityFailureKind
    {
        None,
        MissingEvidence,
        StaleEvidence,
        LowConfidence,
        HardConstraint,
    }

    private readonly record struct EligibilityResult(
        bool IsEligible,
        EligibilityFailureKind FailureKind)
    {
        public static EligibilityResult Eligible() => new(true, EligibilityFailureKind.None);
        public static EligibilityResult Failed(EligibilityFailureKind kind) => new(false, kind);
    }

    private sealed class CandidateEvaluation
    {
        public List<RankedCandidate> Eligible { get; } = [];
        public int Advertised { get; set; }
        public int Unavailable { get; set; }
        public int MissingEvidence { get; private set; }
        public int StaleEvidence { get; private set; }
        public int LowConfidence { get; private set; }
        public int HardConstraint { get; private set; }

        public void RecordFailure(EligibilityFailureKind kind)
        {
            switch (kind)
            {
                case EligibilityFailureKind.MissingEvidence:
                    MissingEvidence++;
                    break;
                case EligibilityFailureKind.StaleEvidence:
                    StaleEvidence++;
                    break;
                case EligibilityFailureKind.LowConfidence:
                    LowConfidence++;
                    break;
                case EligibilityFailureKind.HardConstraint:
                    HardConstraint++;
                    break;
            }
        }

        public CandidateEvaluation Merge(CandidateEvaluation other)
        {
            var result = new CandidateEvaluation
            {
                Advertised = Advertised + other.Advertised,
                Unavailable = Unavailable + other.Unavailable,
                MissingEvidence = MissingEvidence + other.MissingEvidence,
                StaleEvidence = StaleEvidence + other.StaleEvidence,
                LowConfidence = LowConfidence + other.LowConfidence,
                HardConstraint = HardConstraint + other.HardConstraint,
            };
            result.Eligible.AddRange(Eligible);
            result.Eligible.AddRange(other.Eligible);
            return result;
        }
    }
}

public static class ModelAssignmentFingerprint
{
    internal static string Compute(ModelAssignmentDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, decision.CatalogVersion);
        Append(hash, decision.RoleId);
        Append(hash, decision.RolePolicyVersion);
        Append(hash, decision.SelectedModelId);
        Append(hash, decision.SelectedRevision);
        Append(hash, decision.ProviderFamily);
        Append(hash, decision.ProviderModelKey);
        Append(hash, decision.SelectionMode);
        Append(hash, decision.WeightedScoreBasisPoints);
        Append(hash, decision.ProfileFingerprint);
        Append(hash, decision.ReasonCodes);
        Append(hash, decision.EligibleCandidateCount);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static bool Verify(ModelAssignmentDecision decision)
    {
        if (decision is null || !ModelBenchmarkCatalog.IsLowerHexSha256(decision.AssignmentFingerprint))
        {
            return false;
        }
        var expected = Compute(decision);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(decision.AssignmentFingerprint));
    }

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, string value)
    {
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

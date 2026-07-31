using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Application.Authoring;

public sealed class VisualAuditOrchestrator
{
    private readonly IVisualAuditPolicyCatalog _policies;
    private readonly IReadOnlyList<IVisualAuditCheckProvider> _providers;
    private readonly IVisualAuditStore _store;

    public VisualAuditOrchestrator(
        IVisualAuditPolicyCatalog policies,
        IEnumerable<IVisualAuditCheckProvider> providers,
        IVisualAuditStore store)
    {
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        _providers = providers?.ToArray() ?? throw new ArgumentNullException(nameof(providers));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async ValueTask<VisualAuditState> ExecuteAsync(
        VisualAuditRequest request,
        DateTimeOffset startedAtUtc,
        CancellationToken ct = default)
    {
        ValidateRequest(request);
        var policy = _policies.Resolve(request.PolicyId, request.PolicyVersion);
        ValidatePolicy(request, policy);

        var submission = await _store.SubmitAsync(request, startedAtUtc, ct);
        if (submission.Replayed && submission.Audit.Status is VisualAuditStatus.Completed
            or VisualAuditStatus.RepairRequired or VisualAuditStatus.Blocked)
            return submission.Audit;

        var executionId = DeterministicGuid(request.AuditId, $"execution:{policy.PolicyDigest}");
        var execution = new VisualAuditExecution(request, policy, executionId, startedAtUtc);
        var checks = new List<VisualAuditCheckResult>();

        foreach (var kind in request.RequestedChecks.OrderBy(static x => x))
        {
            ct.ThrowIfCancellationRequested();
            var provider = ResolveProvider(kind);
            var results = await provider.ExecuteAsync(execution, ct);
            var matching = results.Where(result => result.Kind == kind).ToArray();
            if (matching.Length != 1)
                throw new VisualAuditValidationException($"Check '{kind}' must produce exactly one normalized result.");
            ValidateCheck(policy, provider, matching[0]);
            checks.Add(matching[0]);
        }

        var state = await _store.RecordChecksAsync(new VisualAuditCheckBatch(
            request.AuditId,
            request.WorkspaceId,
            submission.Audit.Revision,
            executionId,
            checks,
            request.Actor,
            request.RequestFingerprint),
            checks.Max(static x => x.CompletedAtUtc),
            ct);

        var findings = BuildFindings(request.AuditId, policy, checks);
        var outcome = Aggregate(policy, checks, findings);
        var aggregationEvidence = BuildAggregationEvidence(policy, checks, findings, outcome);

        return await _store.CompleteAsync(new VisualAuditCompletion(
            request.AuditId,
            request.WorkspaceId,
            state.Revision,
            outcome,
            findings,
            aggregationEvidence,
            Hash(aggregationEvidence),
            request.Actor,
            request.RequestFingerprint),
            checks.Max(static x => x.CompletedAtUtc),
            ct);
    }

    private IVisualAuditCheckProvider ResolveProvider(VisualAuditCheckKind kind)
    {
        var matches = _providers.Where(provider => provider.SupportedChecks.Contains(kind)).ToArray();
        if (matches.Length != 1)
            throw new VisualAuditValidationException($"Check '{kind}' requires exactly one provider.");
        return matches[0];
    }

    private static void ValidateRequest(VisualAuditRequest request)
    {
        if (request.AuditId == Guid.Empty || request.ProjectId == Guid.Empty
            || request.AssetId == Guid.Empty || request.VisualBriefId == Guid.Empty)
            throw new VisualAuditValidationException("Complete audit, project, asset and visual-brief identity is required.");
        RequireText(request.WorkspaceId, request.ExpectedAssetDigest,
            request.ExpectedVisualBriefDigest, request.PolicyId, request.PolicyVersion,
            request.Actor, request.RequestFingerprint);
        if (request.ExpectedAssetRevision < 1 || request.ExpectedVisualBriefRevision < 1)
            throw new VisualAuditValidationException("Exact positive asset and visual-brief revisions are required.");
        if (request.RequestedChecks.Count == 0)
            throw new VisualAuditValidationException("At least one visual audit check is required.");
        if (request.AdapterRequestId.HasValue && string.IsNullOrWhiteSpace(request.AdapterEvidenceDigest))
            throw new VisualAuditValidationException("Adapter-linked audits require immutable adapter evidence.");
    }

    private static void ValidatePolicy(VisualAuditRequest request, VisualAuditPolicy policy)
    {
        RequireText(policy.PolicyId, policy.PolicyVersion, policy.PolicyDigest);
        if (!StringComparer.Ordinal.Equals(policy.PolicyId, request.PolicyId)
            || !StringComparer.Ordinal.Equals(policy.PolicyVersion, request.PolicyVersion))
            throw new VisualAuditValidationException("Resolved audit policy identity does not match the request.");
        if (!policy.RequiredChecks.IsSubsetOf(request.RequestedChecks))
            throw new VisualAuditValidationException("Requested checks do not provide complete policy coverage.");
        if (policy.MinimumSemanticConfidence is < 0 or > 1)
            throw new VisualAuditValidationException("Semantic confidence threshold must be between zero and one.");
        if (policy.MaximumWaiverDuration <= TimeSpan.Zero)
            throw new VisualAuditValidationException("Waiver duration must be positive and bounded.");
    }

    private static void ValidateCheck(
        VisualAuditPolicy policy,
        IVisualAuditCheckProvider provider,
        VisualAuditCheckResult result)
    {
        if (result.CheckId == Guid.Empty)
            throw new VisualAuditValidationException("Check identity is required.");
        RequireText(result.PolicyId, result.PolicyVersion, result.Evidence,
            result.EvidenceDigest, result.ProviderId, result.ProviderVersion);
        if (!StringComparer.Ordinal.Equals(result.PolicyId, policy.PolicyId)
            || !StringComparer.Ordinal.Equals(result.PolicyVersion, policy.PolicyVersion))
            throw new VisualAuditValidationException("Check result policy identity does not match the resolved policy.");
        if (!StringComparer.Ordinal.Equals(result.ProviderId, provider.ProviderId)
            || !StringComparer.Ordinal.Equals(result.ProviderVersion, provider.ProviderVersion))
            throw new VisualAuditValidationException("Check result provider identity does not match the executing provider.");
        if (result.Confidence is < 0 or > 1)
            throw new VisualAuditValidationException("Check confidence must be between zero and one.");
        if (result.Outcome is VisualAuditCheckOutcome.Fail or VisualAuditCheckOutcome.HumanReviewRequired
            && result.FindingCode is null)
            throw new VisualAuditValidationException("Failed or escalated checks require a normalized finding code.");
        if (!StringComparer.OrdinalIgnoreCase.Equals(Hash(result.Evidence), result.EvidenceDigest))
            throw new VisualAuditValidationException("Check evidence digest is invalid.");
    }

    private static IReadOnlyList<VisualAuditFinding> BuildFindings(
        Guid auditId,
        VisualAuditPolicy policy,
        IReadOnlyList<VisualAuditCheckResult> checks)
    {
        var findings = new List<VisualAuditFinding>();
        foreach (var check in checks.Where(static check => check.Outcome != VisualAuditCheckOutcome.Pass))
        {
            var code = check.FindingCode ?? (check.Outcome is VisualAuditCheckOutcome.Unknown
                or VisualAuditCheckOutcome.Skipped or VisualAuditCheckOutcome.Partial
                    ? VisualAuditFindingCode.IncompleteCoverage
                    : VisualAuditFindingCode.UnevidencedCheck);
            findings.Add(new VisualAuditFinding(
                DeterministicGuid(auditId, $"finding:{check.CheckId:D}:{code}"),
                check.CheckId,
                code,
                check.Severity,
                $"{check.Kind} returned {check.Outcome}.",
                check.Evidence,
                check.EvidenceDigest,
                !policy.NonWaivableFindings.Contains(code),
                check.RepairRecommendation));
        }
        return findings;
    }

    private static VisualAuditAggregateOutcome Aggregate(
        VisualAuditPolicy policy,
        IReadOnlyList<VisualAuditCheckResult> checks,
        IReadOnlyList<VisualAuditFinding> findings)
    {
        if (!policy.RequiredChecks.All(required => checks.Count(check => check.Kind == required) == 1))
            return VisualAuditAggregateOutcome.Blocked;
        if (checks.Any(check => check.Outcome is VisualAuditCheckOutcome.Unknown
            or VisualAuditCheckOutcome.Skipped or VisualAuditCheckOutcome.Partial))
            return VisualAuditAggregateOutcome.Blocked;
        if (findings.Any(finding => policy.NonWaivableFindings.Contains(finding.Code)
            || finding.Severity == VisualAuditSeverity.Blocking))
            return VisualAuditAggregateOutcome.Blocked;
        if (checks.Any(check => check.Outcome == VisualAuditCheckOutcome.HumanReviewRequired
            || policy.HumanReviewChecks.Contains(check.Kind)
            || (IsSemantic(check.Kind) && check.Confidence < policy.MinimumSemanticConfidence)))
            return VisualAuditAggregateOutcome.HumanReviewRequired;
        if (checks.Any(check => check.Outcome == VisualAuditCheckOutcome.Fail))
            return VisualAuditAggregateOutcome.RepairRequired;
        return VisualAuditAggregateOutcome.Pass;
    }

    private static bool IsSemantic(VisualAuditCheckKind kind) => kind is
        VisualAuditCheckKind.VisualBriefConformance or VisualAuditCheckKind.SubjectIdentity
        or VisualAuditCheckKind.Continuity or VisualAuditCheckKind.ProhibitedElements
        or VisualAuditCheckKind.GenreChannelFitness or VisualAuditCheckKind.ProvenanceCompleteness
        or VisualAuditCheckKind.RightsCompleteness or VisualAuditCheckKind.AccessibilityPrerequisites;

    private static string BuildAggregationEvidence(
        VisualAuditPolicy policy,
        IReadOnlyList<VisualAuditCheckResult> checks,
        IReadOnlyList<VisualAuditFinding> findings,
        VisualAuditAggregateOutcome outcome) =>
        $"policy={policy.PolicyId}@{policy.PolicyVersion};digest={policy.PolicyDigest};" +
        $"checks={checks.Count};findings={findings.Count};outcome={outcome};" +
        $"check-digests={string.Join(',', checks.OrderBy(static x => x.Kind).Select(static x => x.EvidenceDigest))}";

    private static Guid DeterministicGuid(Guid scope, string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{scope:D}:{value}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void RequireText(params string[] values)
    {
        if (values.Any(string.IsNullOrWhiteSpace))
            throw new VisualAuditValidationException("Required visual audit evidence is missing.");
    }
}
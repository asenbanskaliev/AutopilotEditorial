using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Application.Production;

public sealed class AccessibilityOrchestrator
{
    private readonly IAccessibilityStore _store;
    private readonly IAccessibilityAuthorityReader _authority;
    private readonly IReadOnlyList<IAccessibilityAnalyzer> _analyzers;

    public AccessibilityOrchestrator(IAccessibilityStore store, IAccessibilityAuthorityReader authority, IEnumerable<IAccessibilityAnalyzer> analyzers)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _analyzers = (analyzers ?? throw new ArgumentNullException(nameof(analyzers)))
            .OrderBy(x => x.AnalyzerId, StringComparer.Ordinal).ThenBy(x => x.Version, StringComparer.Ordinal).ToArray();
        if (_analyzers.Count == 0) throw new ArgumentException("At least one accessibility analyzer is required.", nameof(analyzers));
        if (_analyzers.Select(x => $"{x.AnalyzerId}@{x.Version}").Distinct(StringComparer.Ordinal).Count() != _analyzers.Count)
            throw new ArgumentException("Accessibility analyzer identities must be unique.", nameof(analyzers));
    }

    public async ValueTask<AccessibilityState> AnalyzeAsync(AccessibilityRequest request, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(request);
        var authority = await _authority.RequireCurrentApprovedAsync(request.Authority, ct);
        if (!authority.IsCurrent || !authority.IsApproved || authority.Authority != request.Authority)
            throw new AccessibilityValidationException("Accessibility authority is stale, mismatched or not approved.");

        var input = new AccessibilityAnalysisInput(request.RunId, request.ProjectId, request.WorkspaceId,
            request.Authority.ArtifactDigest, request.Locale, request.TargetProfiles.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        var results = new List<AccessibilityAnalyzerResult>(_analyzers.Count);
        foreach (var analyzer in _analyzers)
        {
            var result = await analyzer.AnalyzeAsync(input, ct);
            ValidateAnalyzerResult(analyzer, result);
            results.Add(result);
        }

        var evidence = BuildEvidence(request, results);
        return (await _store.SubmitAsync(request, evidence, at, ct)).Run;
    }

    public async ValueTask<AccessibilityState> ReviewAsync(AccessibilityReviewCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        var current = await RequireAsync(command.WorkspaceId, command.RunId, ct);
        RequireRevision(current, command.ExpectedRevision);
        await RequireCurrentAuthorityAsync(current, ct);
        if (command.Reviews.Any(x => !x.Completed || string.IsNullOrWhiteSpace(x.Reviewer) || string.IsNullOrWhiteSpace(x.EvidenceDigest)))
            throw new AccessibilityValidationException("Completed manual reviews require reviewer and evidence.");
        if (command.Waivers.Any(x => x.ExpiresAtUtc <= at || string.IsNullOrWhiteSpace(x.ApprovedBy) || string.IsNullOrWhiteSpace(x.EvidenceDigest)))
            throw new AccessibilityValidationException("Accessibility waivers must be bounded, approved and evidenced.");
        return await _store.ReviewAsync(command, at, ct);
    }

    public async ValueTask<AccessibilityState> DecideAsync(AccessibilityDecisionCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        var current = await RequireAsync(command.WorkspaceId, command.RunId, ct);
        RequireRevision(current, command.ExpectedRevision);
        await RequireCurrentAuthorityAsync(current, ct);
        if (string.IsNullOrWhiteSpace(command.Reason) || string.IsNullOrWhiteSpace(command.EvidenceDigest))
            throw new AccessibilityValidationException("Decision evidence is required.");
        if (command.Decision == AccessibilityDecision.Approve)
        {
            var blocking = current.Evidence.Findings.Any(x => x.Severity == AccessibilitySeverity.Blocking && x.RemediationStatus == AccessibilityRemediationStatus.Open);
            var incomplete = current.Evidence.ManualReviews.Any(x => !x.Completed || x.Disposition is AccessibilityManualReviewDisposition.Pending or AccessibilityManualReviewDisposition.Fail);
            if (blocking || incomplete) throw new AccessibilityTransitionException("Accessibility cannot be approved with blocking findings or incomplete manual review.");
        }
        return await _store.DecideAsync(command, at, ct);
    }

    public static AccessibilityEvidence BuildEvidence(AccessibilityRequest request, IReadOnlyList<AccessibilityAnalyzerResult> results)
    {
        var ordered = results.OrderBy(x => x.AnalyzerId, StringComparer.Ordinal).ThenBy(x => x.AnalyzerVersion, StringComparer.Ordinal).ToArray();
        var executions = ordered.Select(x => new AccessibilityAnalyzerExecution(x.AnalyzerId, x.AnalyzerVersion, x.RuleProfile, x.InputDigest, x.OutputDigest, x.Findings.Count)).ToArray();
        var findings = ordered.SelectMany(x => x.Findings).OrderBy(x => x.RuleId, StringComparer.Ordinal).ThenBy(x => x.Location, StringComparer.Ordinal).ThenBy(x => x.FindingId).ToArray();
        var reviews = request.ManualReviewRequirements.OrderBy(x => x.Scope, StringComparer.Ordinal).ThenBy(x => x.ReviewId)
            .Select(x => new AccessibilityManualReview(x.ReviewId, x.Scope, string.Empty, string.Empty, string.Empty, AccessibilityManualReviewDisposition.Pending, !x.Required)).ToArray();
        var token = string.Join("|", executions.Select(x => $"{x.AnalyzerId}@{x.AnalyzerVersion}:{x.RuleProfile}:{x.InputDigest}:{x.OutputDigest}:{x.FindingCount}"))
            + "||" + string.Join("|", findings.Select(x => $"{x.RuleId}:{x.Category}:{x.Severity}:{x.Location}:{x.EvidenceDigest}:{x.RemediationStatus}"))
            + "||" + string.Join("|", reviews.Select(x => $"{x.ReviewId:D}:{x.Scope}:{x.Completed}:{x.Disposition}"))
            + "||" + request.Authority.ArtifactDigest;
        return new AccessibilityEvidence(Digest(token), executions, findings, reviews, Array.Empty<AccessibilityWaiver>());
    }

    private async ValueTask<AccessibilityState> RequireAsync(string workspaceId, Guid runId, CancellationToken ct) =>
        await _store.GetAsync(workspaceId, runId, ct) ?? throw new AccessibilityValidationException("Accessibility run not found.");

    private async ValueTask RequireCurrentAuthorityAsync(AccessibilityState current, CancellationToken ct)
    {
        var snapshot = await _authority.RequireCurrentApprovedAsync(current.Authority, ct);
        if (!snapshot.IsCurrent || !snapshot.IsApproved || snapshot.Authority != current.Authority)
            throw new AccessibilityValidationException("Accessibility authority is no longer current and approved.");
    }

    private static void ValidateRequest(AccessibilityRequest request)
    {
        if (request.RequestId == Guid.Empty || request.RunId == Guid.Empty || request.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(request.WorkspaceId))
            throw new AccessibilityValidationException("Accessibility identity is required.");
        if (request.Authority.WorkspaceId != request.WorkspaceId || request.Authority.ProjectId != request.ProjectId)
            throw new AccessibilityValidationException("Cross-workspace or cross-project authority is forbidden.");
        if (!string.Equals(request.Authority.Status, "Approved", StringComparison.Ordinal))
            throw new AccessibilityValidationException("Accessibility requires approved DOCX authority.");
        if (request.TargetProfiles.Count == 0 || request.TargetProfiles.Any(string.IsNullOrWhiteSpace))
            throw new AccessibilityValidationException("At least one accessibility target profile is required.");
        if (request.TargetProfiles.Distinct(StringComparer.Ordinal).Count() != request.TargetProfiles.Count)
            throw new AccessibilityValidationException("Accessibility target profiles must be unique.");
        if (request.ManualReviewRequirements.Select(x => x.ReviewId).Distinct().Count() != request.ManualReviewRequirements.Count)
            throw new AccessibilityValidationException("Manual review identities must be unique.");
    }

    private static void ValidateAnalyzerResult(IAccessibilityAnalyzer analyzer, AccessibilityAnalyzerResult result)
    {
        if (!string.Equals(result.AnalyzerId, analyzer.AnalyzerId, StringComparison.Ordinal) || !string.Equals(result.AnalyzerVersion, analyzer.Version, StringComparison.Ordinal))
            throw new AccessibilityValidationException("Analyzer result identity does not match the invoked analyzer.");
        if (string.IsNullOrWhiteSpace(result.RuleProfile) || string.IsNullOrWhiteSpace(result.InputDigest) || string.IsNullOrWhiteSpace(result.OutputDigest))
            throw new AccessibilityValidationException("Analyzer identity, profile and digests are required.");
        if (result.Findings.Any(x => x.FindingId == Guid.Empty || string.IsNullOrWhiteSpace(x.RuleId) || string.IsNullOrWhiteSpace(x.EvidenceDigest)))
            throw new AccessibilityValidationException("Analyzer findings require stable identity and evidence.");
    }

    private static void RequireRevision(AccessibilityState state, long expected)
    {
        if (state.Revision != expected) throw new AccessibilityConflictException("Stale accessibility run revision.");
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

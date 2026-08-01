using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Application.Production;

public sealed class TechnicalPreflightOrchestrator
{
    private readonly ITechnicalPreflightStore _store;
    private readonly ITechnicalPreflightAuthorityReader _authority;
    private readonly IReadOnlyList<ITechnicalPreflightChecker> _checkers;

    public TechnicalPreflightOrchestrator(ITechnicalPreflightStore store,
        ITechnicalPreflightAuthorityReader authority, IEnumerable<ITechnicalPreflightChecker> checkers)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _checkers = (checkers ?? throw new ArgumentNullException(nameof(checkers)))
            .OrderBy(x => x.CheckerId, StringComparer.Ordinal).ThenBy(x => x.Version, StringComparer.Ordinal).ToArray();
        if (_checkers.Count == 0 || _checkers.Select(x => x.CheckerId).Distinct(StringComparer.Ordinal).Count() != _checkers.Count)
            throw new TechnicalPreflightValidationException("Technical preflight checkers must be non-empty and uniquely identified.");
    }

    public async ValueTask<TechnicalPreflightState> SubmitAsync(TechnicalPreflightRequest request, DateTimeOffset at, CancellationToken ct = default)
    {
        ValidateRequest(request);
        var authority = await _authority.RequireCurrentAsync(request.Authority, ct);
        if (!authority.IsCurrent || request.Authority.Status != TechnicalPreflightAuthorityStatus.Approved)
            throw new TechnicalPreflightValidationException("VS-114 authority is stale, mismatched or not approved.");
        return (await _store.SubmitAsync(request, at, ct)).State;
    }

    public async ValueTask<TechnicalPreflightState> EvaluateAsync(TechnicalPreflightEvaluationCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        var current = await RequireAsync(command.WorkspaceId, command.RunId, ct);
        RequireRevision(current, command.ExpectedRevision);
        var authority = await _authority.RequireCurrentAsync(current.Authority, ct);
        if (!authority.IsCurrent || current.Authority.Status != TechnicalPreflightAuthorityStatus.Approved)
            throw new TechnicalPreflightValidationException("VS-114 authority drifted before evaluation.");
        var executions = await ExecuteCheckersAsync(current, ct);
        var evidenceDigest = BuildEvidenceDigest(executions);
        return await _store.EvaluateAsync(command, executions, evidenceDigest, at, ct);
    }

    public async ValueTask<TechnicalPreflightState> DecideAsync(TechnicalPreflightDecisionCommand command, DateTimeOffset at, CancellationToken ct = default)
    {
        var current = await RequireAsync(command.WorkspaceId, command.RunId, ct);
        RequireRevision(current, command.ExpectedRevision);
        RequireText(command.Reason, command.Evidence, command.EvidenceDigest, command.Actor, command.RequestFingerprint);
        if (command.Decision == TechnicalPreflightDecision.Approve)
            EnsureApprovable(current, command.Waivers, at);
        if (command.Decision == TechnicalPreflightDecision.Supersede && current.Status != TechnicalPreflightStatus.Approved)
            throw new TechnicalPreflightTransitionException("Only an approved technical preflight can be superseded.");
        return await _store.DecideAsync(command, at, ct);
    }

    public async ValueTask<IReadOnlyList<TechnicalPreflightCheckResult>> ExecuteCheckersAsync(TechnicalPreflightState state, CancellationToken ct = default)
    {
        var authority = await _authority.RequireCurrentAsync(state.Authority, ct);
        if (!authority.IsCurrent) throw new TechnicalPreflightValidationException("Authority drifted before checker execution.");
        var context = new TechnicalPreflightCheckContext(state.RunId, state.ProjectId, state.WorkspaceId,
            state.ProductionArtifactDigest, state.TargetProfile, state.Locale, state.RuleProfile, authority.AuthorityDigest);
        var results = new List<TechnicalPreflightCheckResult>(_checkers.Count);
        foreach (var checker in _checkers)
        {
            var result = await checker.ExecuteAsync(context, ct);
            if (!StringComparer.Ordinal.Equals(result.CheckerId, checker.CheckerId) ||
                !StringComparer.Ordinal.Equals(result.CheckerVersion, checker.Version) ||
                !StringComparer.Ordinal.Equals(result.RuleProfile, state.RuleProfile) ||
                string.IsNullOrWhiteSpace(result.InputDigest) || string.IsNullOrWhiteSpace(result.OutputDigest))
                throw new TechnicalPreflightValidationException("Checker returned invalid or mismatched evidence.");
            results.Add(result with { Findings = result.Findings.OrderBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.FindingId).ToArray() });
        }
        return results;
    }

    public static string BuildEvidenceDigest(IEnumerable<TechnicalPreflightCheckResult> executions)
    {
        var canonical = string.Join("\n", executions.OrderBy(x => x.CheckerId, StringComparer.Ordinal)
            .ThenBy(x => x.CheckerVersion, StringComparer.Ordinal)
            .Select(x => $"{x.CheckerId}|{x.CheckerVersion}|{x.RuleProfile}|{x.InputDigest}|{x.OutputDigest}|{string.Join(',', x.Findings.OrderBy(f => f.Code, StringComparer.Ordinal).ThenBy(f => f.FindingId).Select(f => $"{f.FindingId:D}:{f.Code}:{f.Severity}:{f.Location}:{f.RuleId}:{f.EvidenceDigest}:{f.RemediationStatus}"))}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private async ValueTask<TechnicalPreflightState> RequireAsync(string workspaceId, Guid runId, CancellationToken ct) =>
        await _store.GetAsync(workspaceId, runId, ct) ?? throw new TechnicalPreflightValidationException("Technical preflight run not found.");

    private static void EnsureApprovable(TechnicalPreflightState state, IReadOnlyList<TechnicalPreflightWaiver> waivers, DateTimeOffset at)
    {
        if (state.Status != TechnicalPreflightStatus.Evaluated || string.IsNullOrWhiteSpace(state.EvidenceDigest))
            throw new TechnicalPreflightTransitionException("Only an evaluated preflight with evidence can be approved.");
        var valid = waivers.Where(x => x.ExpiresAtUtc > at && !string.IsNullOrWhiteSpace(x.Evidence) &&
                                      !string.IsNullOrWhiteSpace(x.EvidenceDigest) && !string.IsNullOrWhiteSpace(x.ApprovedBy))
            .Select(x => x.FindingId).ToHashSet();
        if (state.Findings.Any(x => x.Severity == TechnicalPreflightSeverity.Blocking && x.RemediationStatus == TechnicalPreflightRemediationStatus.Open && !valid.Contains(x.FindingId)))
            throw new TechnicalPreflightTransitionException("Open blocking findings prevent approval.");
    }

    private static void ValidateRequest(TechnicalPreflightRequest request)
    {
        if (request.RequestId == Guid.Empty || request.RunId == Guid.Empty || request.ProjectId == Guid.Empty ||
            request.Authority.AccessibilityRunId == Guid.Empty || request.Authority.AccessibilityRevision <= 0)
            throw new TechnicalPreflightValidationException("Stable technical preflight identity is required.");
        RequireText(request.WorkspaceId, request.Authority.AccessibilityEvidenceDigest, request.ProductionArtifactDigest,
            request.TargetProfile, request.Locale, request.RuleProfile, request.Actor, request.RequestFingerprint);
        if (!StringComparer.Ordinal.Equals(request.WorkspaceId, request.Authority.WorkspaceId) || request.ProjectId != request.Authority.ProjectId)
            throw new TechnicalPreflightValidationException("Cross-workspace or cross-project authority is forbidden.");
    }

    private static void RequireRevision(TechnicalPreflightState state, long expected)
    {
        if (state.Revision != expected) throw new TechnicalPreflightConflictException("Stale technical preflight revision.");
    }

    private static void RequireText(params string[] values)
    {
        if (values.Any(string.IsNullOrWhiteSpace)) throw new TechnicalPreflightValidationException("Required technical preflight text is missing.");
    }
}

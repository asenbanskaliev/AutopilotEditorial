namespace BookStudio.Application.Production;

public interface ITechnicalPreflightStore
{
    ValueTask<TechnicalPreflightSubmissionResult> SubmitAsync(TechnicalPreflightRequest request, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<TechnicalPreflightState> EvaluateAsync(TechnicalPreflightEvaluationCommand command, IReadOnlyList<TechnicalPreflightCheckResult> executions, string evidenceDigest, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<TechnicalPreflightState> DecideAsync(TechnicalPreflightDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<TechnicalPreflightState?> GetAsync(string workspaceId, Guid runId, CancellationToken ct = default);
}

public interface ITechnicalPreflightAuthorityReader
{
    ValueTask<TechnicalPreflightAuthoritySnapshot> RequireCurrentAsync(TechnicalPreflightAuthority authority, CancellationToken ct = default);
}

public interface ITechnicalPreflightChecker
{
    string CheckerId { get; }
    string Version { get; }
    ValueTask<TechnicalPreflightCheckResult> ExecuteAsync(TechnicalPreflightCheckContext context, CancellationToken ct = default);
}

public sealed record TechnicalPreflightRequest(Guid RequestId, Guid RunId, Guid ProjectId, string WorkspaceId,
    TechnicalPreflightAuthority Authority, string ProductionArtifactDigest, string TargetProfile, string Locale,
    string RuleProfile, string Actor, string RequestFingerprint);

public sealed record TechnicalPreflightAuthority(Guid AccessibilityRunId, long AccessibilityRevision,
    string AccessibilityEvidenceDigest, string WorkspaceId, Guid ProjectId, TechnicalPreflightAuthorityStatus Status);

public sealed record TechnicalPreflightAuthoritySnapshot(TechnicalPreflightAuthority Authority, bool IsCurrent,
    string AuthorityDigest, DateTimeOffset VerifiedAtUtc);

public sealed record TechnicalPreflightCheckContext(Guid RunId, Guid ProjectId, string WorkspaceId,
    string ProductionArtifactDigest, string TargetProfile, string Locale, string RuleProfile, string AuthorityDigest);

public sealed record TechnicalPreflightCheckResult(string CheckerId, string CheckerVersion, string RuleProfile,
    string InputDigest, string OutputDigest, IReadOnlyList<TechnicalPreflightFinding> Findings);

public sealed record TechnicalPreflightFinding(Guid FindingId, string Code, TechnicalPreflightSeverity Severity,
    string Location, string RuleId, string Description, string EvidenceDigest, TechnicalPreflightRemediationStatus RemediationStatus);

public sealed record TechnicalPreflightWaiver(Guid WaiverId, Guid FindingId, string Reason, string Evidence,
    string EvidenceDigest, DateTimeOffset ExpiresAtUtc, string ApprovedBy, DateTimeOffset ApprovedAtUtc);

public sealed record TechnicalPreflightEvaluationCommand(Guid RequestId, Guid RunId, string WorkspaceId,
    long ExpectedRevision, string Actor, string RequestFingerprint);

public sealed record TechnicalPreflightDecisionCommand(Guid RequestId, Guid RunId, string WorkspaceId,
    long ExpectedRevision, TechnicalPreflightDecision Decision, string Reason, string Evidence,
    string EvidenceDigest, IReadOnlyList<TechnicalPreflightWaiver> Waivers, string Actor, string RequestFingerprint);

public sealed record TechnicalPreflightState(Guid RunId, Guid ProjectId, string WorkspaceId,
    TechnicalPreflightAuthority Authority, string ProductionArtifactDigest, string TargetProfile, string Locale,
    string RuleProfile, IReadOnlyList<TechnicalPreflightCheckResult> Executions,
    IReadOnlyList<TechnicalPreflightFinding> Findings, IReadOnlyList<TechnicalPreflightWaiver> Waivers,
    string? EvidenceDigest, TechnicalPreflightStatus Status, long Revision, Guid? MessageId,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record TechnicalPreflightSubmissionResult(TechnicalPreflightState State, bool Replayed);

public enum TechnicalPreflightAuthorityStatus { Approved, Rejected, Superseded }
public enum TechnicalPreflightSeverity { Advisory, Major, Blocking }
public enum TechnicalPreflightRemediationStatus { Open, Resolved, Waived }
public enum TechnicalPreflightDecision { Approve, ReturnToRepair, Reject, Supersede }
public enum TechnicalPreflightStatus { Draft, Evaluated, Approved, RepairRequired, Rejected, Superseded }

public sealed class TechnicalPreflightValidationException : Exception { public TechnicalPreflightValidationException(string message) : base(message) { } }
public sealed class TechnicalPreflightConflictException : Exception { public TechnicalPreflightConflictException(string message) : base(message) { } }
public sealed class TechnicalPreflightTransitionException : Exception { public TechnicalPreflightTransitionException(string message) : base(message) { } }

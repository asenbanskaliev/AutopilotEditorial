namespace BookStudio.Application.Production;

public interface IAccessibilityStore
{
    ValueTask<AccessibilitySubmissionResult> SubmitAsync(AccessibilityRequest request, AccessibilityEvidence evidence, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<AccessibilityState> ReviewAsync(AccessibilityReviewCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<AccessibilityState> DecideAsync(AccessibilityDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<AccessibilityState?> GetAsync(string workspaceId, Guid runId, CancellationToken ct = default);
}

public interface IAccessibilityAuthorityReader
{
    ValueTask<AccessibilityAuthoritySnapshot> RequireCurrentApprovedAsync(AccessibilityAuthority authority, CancellationToken ct = default);
}

public interface IAccessibilityAnalyzer
{
    string AnalyzerId { get; }
    string Version { get; }
    ValueTask<AccessibilityAnalyzerResult> AnalyzeAsync(AccessibilityAnalysisInput input, CancellationToken ct = default);
}

public sealed record AccessibilityRequest(Guid RequestId, Guid RunId, Guid ProjectId, string WorkspaceId, AccessibilityAuthority Authority, string Locale, IReadOnlyList<string> TargetProfiles, IReadOnlyList<AccessibilityManualReviewRequirement> ManualReviewRequirements, string Actor, string RequestFingerprint);
public sealed record AccessibilityAuthority(Guid DocxRenderId, long Revision, string ArtifactDigest, string WorkspaceId, Guid ProjectId, string Status);
public sealed record AccessibilityAuthoritySnapshot(AccessibilityAuthority Authority, bool IsCurrent, bool IsApproved, DateTimeOffset VerifiedAtUtc);
public sealed record AccessibilityAnalysisInput(Guid RunId, Guid ProjectId, string WorkspaceId, string ArtifactDigest, string Locale, IReadOnlyList<string> TargetProfiles);
public sealed record AccessibilityAnalyzerResult(string AnalyzerId, string AnalyzerVersion, string RuleProfile, string InputDigest, string OutputDigest, IReadOnlyList<AccessibilityFinding> Findings);
public sealed record AccessibilityAnalyzerExecution(string AnalyzerId, string AnalyzerVersion, string RuleProfile, string InputDigest, string OutputDigest, int FindingCount);
public sealed record AccessibilityFinding(Guid FindingId, string RuleId, AccessibilityFindingCategory Category, AccessibilitySeverity Severity, string Location, string Description, string EvidenceDigest, AccessibilityRemediationStatus RemediationStatus);
public sealed record AccessibilityManualReviewRequirement(Guid ReviewId, string Scope, string EvidenceRequirement, bool Required);
public sealed record AccessibilityManualReview(Guid ReviewId, string Scope, string Reviewer, string Evidence, string EvidenceDigest, AccessibilityManualReviewDisposition Disposition, bool Completed);
public sealed record AccessibilityWaiver(Guid WaiverId, Guid FindingId, string Scope, string Reason, string EvidenceDigest, DateTimeOffset ExpiresAtUtc, string ApprovedBy);
public sealed record AccessibilityEvidence(string EvidenceDigest, IReadOnlyList<AccessibilityAnalyzerExecution> Executions, IReadOnlyList<AccessibilityFinding> Findings, IReadOnlyList<AccessibilityManualReview> ManualReviews, IReadOnlyList<AccessibilityWaiver> Waivers);
public sealed record AccessibilityReviewCommand(Guid RequestId, Guid RunId, string WorkspaceId, long ExpectedRevision, IReadOnlyList<AccessibilityManualReview> Reviews, IReadOnlyList<AccessibilityWaiver> Waivers, string Actor, string RequestFingerprint);
public sealed record AccessibilityDecisionCommand(Guid RequestId, Guid RunId, string WorkspaceId, long ExpectedRevision, AccessibilityDecision Decision, string Reason, string Evidence, string EvidenceDigest, string Actor, string RequestFingerprint);
public sealed record AccessibilityState(Guid RunId, Guid ProjectId, string WorkspaceId, AccessibilityAuthority Authority, string Locale, IReadOnlyList<string> TargetProfiles, AccessibilityEvidence Evidence, AccessibilityStatus Status, long Revision, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record AccessibilitySubmissionResult(AccessibilityState Run, bool Replayed);

public enum AccessibilityDecision { Approve, ReturnToRepair, Reject, Supersede }
public enum AccessibilityStatus { Analyzed, ReviewRequired, Approved, RepairRequired, Rejected, Superseded }
public enum AccessibilityFindingCategory { Structure, ReadingOrder, Heading, Language, Navigation, Link, Table, Image, Alternative, Contrast }
public enum AccessibilitySeverity { Advisory, Major, Blocking }
public enum AccessibilityRemediationStatus { Open, Remediated, Waived }
public enum AccessibilityManualReviewDisposition { Pending, Pass, Fail, NotApplicable }
public sealed class AccessibilityValidationException(string message) : Exception(message);
public sealed class AccessibilityConflictException(string message) : Exception(message);
public sealed class AccessibilityTransitionException(string message) : Exception(message);

namespace BookStudio.Application.Authoring;

public interface IClaimVerificationStore
{
    ValueTask<ClaimVerificationCreateResult> CreateAsync(ClaimVerificationDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ClaimVerification> EvaluateAsync(ClaimVerificationEvaluateCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ClaimVerification> DecideAsync(ClaimVerificationDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ClaimVerification> ReopenAsync(ClaimVerificationReopenCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ClaimVerification> MarkStaleAsync(ClaimVerificationStaleCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ClaimVerification?> GetAsync(string workspaceId, Guid verificationId, CancellationToken ct = default);
}

public sealed record ClaimVerificationDraft(Guid VerificationId, Guid ProjectId, string WorkspaceId, Guid ResearchPlanId, long ExpectedResearchPlanRevision, string ExpectedResearchPlanDigest, Guid ClaimId, ClaimType ClaimType, string Location, int Version, string RuleSet, string Actor, string SnapshotJson, string RequestFingerprint);
public sealed record ClaimVerificationEvaluateCommand(Guid RequestId, string WorkspaceId, Guid VerificationId, long ExpectedRevision, IReadOnlyList<ClaimEvidenceDraft> Evidence, string EvaluationNote, string Actor, string RequestFingerprint);
public sealed record ClaimVerificationDecisionCommand(Guid RequestId, string WorkspaceId, Guid VerificationId, long ExpectedRevision, ClaimVerificationDecision Decision, string Reason, int? ExpectedResearchRevision, string Actor, string RequestFingerprint);
public sealed record ClaimVerificationReopenCommand(Guid RequestId, string WorkspaceId, Guid VerificationId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);
public sealed record ClaimVerificationStaleCommand(Guid RequestId, string WorkspaceId, Guid VerificationId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);

public sealed record ClaimEvidenceDraft(Guid EvidenceId, EvidenceDisposition Disposition, string SourceType, string SourceReference, DateTimeOffset ConsultedAtUtc, DateTimeOffset? ValidUntilUtc, EvidenceQuality Quality, EvidenceCoverage Coverage, decimal Confidence, string Location, string ExtractOrSummary, string ReproducibilityData, bool IsOpen = true);
public sealed record ClaimEvidence(Guid EvidenceId, EvidenceDisposition Disposition, string SourceType, string SourceReference, DateTimeOffset ConsultedAtUtc, DateTimeOffset? ValidUntilUtc, EvidenceQuality Quality, EvidenceCoverage Coverage, decimal Confidence, string Location, string ExtractOrSummary, string ReproducibilityData, bool IsOpen);

public sealed record ClaimVerification(Guid VerificationId, Guid ProjectId, string WorkspaceId, Guid ResearchPlanId, long ExpectedResearchPlanRevision, string ExpectedResearchPlanDigest, Guid ClaimId, ClaimType ClaimType, string Location, int Version, string RuleSet, string Actor, string SnapshotJson, long Revision, ClaimVerificationStatus Status, IReadOnlyList<ClaimEvidence> Evidence, ClaimVerificationDecision? Decision, string? DecisionReason, int? ExpectedResearchRevision, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record ClaimVerificationCreateResult(ClaimVerification Verification, bool Replayed);

public enum ClaimVerificationStatus { Proposed, Evaluated, Verified, Refuted, Inconclusive, ResearchRequired, Stale }
public enum ClaimVerificationDecision { Verified, Refuted, Inconclusive, ReturnToResearch }
public enum ClaimType { Factual, Statistical, Historical, Scientific, Legal, Biographical, Geographic, Temporal, Quotation, Attribution, Other }
public enum EvidenceDisposition { Supports, Refutes, Contextual, Unresolved }
public enum EvidenceQuality { Low, Medium, High, Primary }
public enum EvidenceCoverage { Partial, Substantial, Complete }

public sealed class ClaimVerificationValidationException : Exception { public ClaimVerificationValidationException(string message) : base(message) { } }
public sealed class ClaimVerificationConflictException : Exception { public ClaimVerificationConflictException(string message) : base(message) { } }
public sealed class ClaimVerificationTransitionException : Exception { public ClaimVerificationTransitionException(string message) : base(message) { } }

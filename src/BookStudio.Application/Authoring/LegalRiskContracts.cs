namespace BookStudio.Application.Authoring;

public interface ILegalRiskStore
{
    ValueTask<LegalRiskCreateResult> CreateAsync(LegalRiskDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<LegalRiskCase> EvaluateAsync(LegalRiskEvaluateCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<LegalRiskCase> RecordHumanReviewAsync(LegalRiskHumanReviewCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<LegalRiskCase> DecideAsync(LegalRiskDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<LegalRiskCase> ReopenAsync(LegalRiskReopenCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<LegalRiskCase> MarkStaleAsync(LegalRiskStaleCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<LegalRiskCase?> GetAsync(string workspaceId, Guid caseId, CancellationToken ct = default);
}

public sealed record LegalRiskDraft(Guid CaseId, Guid ProjectId, string WorkspaceId, Guid ProvenanceRecordId, long ExpectedProvenanceRevision, string ExpectedProvenanceDigest, Guid SubjectId, string SubjectReference, string SubjectDigest, int SubjectVersion, IReadOnlyList<string> Jurisdictions, string PolicyVersion, string Actor, string SnapshotJson, string RequestFingerprint);
public sealed record LegalRiskEvaluateCommand(Guid RequestId, string WorkspaceId, Guid CaseId, long ExpectedRevision, IReadOnlyList<LegalRiskFindingDraft> Findings, string Evidence, string Actor, string RequestFingerprint);
public sealed record LegalRiskHumanReviewCommand(Guid RequestId, string WorkspaceId, Guid CaseId, long ExpectedRevision, Guid ReviewId, string ReviewerIdentity, string ReviewerRole, string Scope, LegalHumanDecision Decision, string Rationale, string Evidence, string? Conditions, DateTimeOffset? ExpiresAtUtc, string Actor, string RequestFingerprint);
public sealed record LegalRiskDecisionCommand(Guid RequestId, string WorkspaceId, Guid CaseId, long ExpectedRevision, LegalRiskDecision Decision, string Reason, string Actor, string RequestFingerprint);
public sealed record LegalRiskReopenCommand(Guid RequestId, string WorkspaceId, Guid CaseId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);
public sealed record LegalRiskStaleCommand(Guid RequestId, string WorkspaceId, Guid CaseId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);
public sealed record LegalRiskFindingDraft(Guid FindingId, LegalRiskCategory Category, string Citation, string AffectedParty, string Jurisdiction, LegalRiskSeverity Severity, decimal Confidence, string Rationale, string Evidence, string ProposedMitigation, bool PolicyMandatesHumanReview);
public sealed record LegalRiskFinding(Guid FindingId, LegalRiskCategory Category, string Citation, string AffectedParty, string Jurisdiction, LegalRiskSeverity Severity, decimal Confidence, string Rationale, string Evidence, string ProposedMitigation, bool PolicyMandatesHumanReview, bool Resolved);
public sealed record LegalRiskHumanReview(Guid ReviewId, string ReviewerIdentity, string ReviewerRole, string Scope, LegalHumanDecision Decision, string Rationale, string Evidence, string? Conditions, DateTimeOffset? ExpiresAtUtc, DateTimeOffset ReviewedAtUtc);
public sealed record LegalRiskCase(Guid CaseId, Guid ProjectId, string WorkspaceId, Guid ProvenanceRecordId, long ExpectedProvenanceRevision, string ExpectedProvenanceDigest, Guid SubjectId, string SubjectReference, string SubjectDigest, int SubjectVersion, IReadOnlyList<string> Jurisdictions, string PolicyVersion, long Revision, LegalRiskStatus Status, IReadOnlyList<LegalRiskFinding> Findings, IReadOnlyList<LegalRiskHumanReview> Reviews, string? Evidence, LegalRiskDecision? Decision, string? DecisionReason, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record LegalRiskCreateResult(LegalRiskCase Case, bool Replayed);

public enum LegalRiskCategory { PersonPrivacy, Defamation, PublicityRights, Trademark, Copyright, SensitiveClaim, RegulatedContent, ContractualRestriction, Other }
public enum LegalRiskSeverity { Low, Medium, High, Critical, Unknown }
public enum LegalRiskStatus { Proposed, Evaluated, HumanReviewRequired, Approved, Blocked, RepairRequired, Revoked, Stale }
public enum LegalHumanDecision { Approve, ApproveWithConditions, Reject, RequireRepair }
public enum LegalRiskDecision { Approve, Block, ReturnToRepair, Revoke }

public sealed class LegalRiskValidationException : Exception { public LegalRiskValidationException(string message) : base(message) { } }
public sealed class LegalRiskConflictException : Exception { public LegalRiskConflictException(string message) : base(message) { } }
public sealed class LegalRiskTransitionException : Exception { public LegalRiskTransitionException(string message) : base(message) { } }

namespace BookStudio.Application.Publishing;

public interface IProofWorkflowStore
{
    ValueTask<ProofSubmissionResult> SubmitAsync(ProofRequest request, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ProofState> EvaluateAsync(ProofEvaluationCommand command, IReadOnlyList<ProofChecklistExecution> executions,
        IReadOnlyList<ProofFinding> findings, string evidenceDigest, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ProofState> RecordPhysicalReceiptAsync(PhysicalProofReceiptCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ProofState> DecideAsync(ProofDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ProofState?> GetAsync(string workspaceId, Guid proofId, CancellationToken ct = default);
}

public interface IProofPackageAuthorityReader
{
    ValueTask<ProofPackageAuthoritySnapshot> RequireCurrentAsync(ProofPackageAuthority authority, CancellationToken ct = default);
}

public interface IProofChecklist
{
    string ChecklistId { get; }
    string Version { get; }
    ValueTask<IReadOnlyList<ProofFindingInput>> ExecuteAsync(ProofReviewContext context, CancellationToken ct = default);
}

public sealed record ProofRequest(Guid RequestId, Guid ProofId, Guid ProjectId, string WorkspaceId,
    ProofPackageAuthority Authority, ProofType ProofType, string Locale, string Reviewer, string Actor,
    string RequestFingerprint, Guid? SupersedesProofId = null);

public sealed record ProofPackageAuthority(Guid PackageId, long PackageRevision, string PackageEvidenceDigest,
    string PackageDigest, string WorkspaceId, Guid ProjectId, ProofPackageAuthorityStatus Status);

public sealed record ProofPackageAuthoritySnapshot(ProofPackageAuthority Authority, bool IsCurrent,
    string AuthorityDigest, DateTimeOffset VerifiedAtUtc);

public sealed record ProofReviewContext(ProofRequest Request, ProofPackageAuthoritySnapshot Authority);

public sealed record ProofFindingInput(string RuleId, ProofFindingSeverity Severity, string Location,
    string Description, string Annotation, ProofFindingStatus Status, ProofReviewerDisposition Disposition);

public sealed record ProofChecklistExecution(string ChecklistId, string Version, string InputDigest,
    string OutputDigest, DateTimeOffset ExecutedAtUtc);

public sealed record ProofFinding(Guid FindingId, string ChecklistId, string ChecklistVersion, string RuleId,
    ProofFindingSeverity Severity, string Location, string Description, string AnnotationDigest,
    string EvidenceDigest, ProofFindingStatus Status, ProofReviewerDisposition Disposition);

public sealed record ProofEvaluationCommand(Guid RequestId, Guid ProofId, string WorkspaceId,
    long ExpectedRevision, string Actor, string RequestFingerprint);

public sealed record PhysicalProofReceiptCommand(Guid RequestId, Guid ProofId, string WorkspaceId,
    long ExpectedRevision, string Provider, string OrderReference, DateOnly ReceivedDate,
    string InspectedArtifactDigest, string ReviewerAttestation, string Actor, string RequestFingerprint);

public sealed record PhysicalProofReceipt(string Provider, string OrderReference, DateOnly ReceivedDate,
    string InspectedArtifactDigest, string ReviewerAttestation, DateTimeOffset RecordedAtUtc);

public sealed record ProofDecisionCommand(Guid RequestId, Guid ProofId, string WorkspaceId,
    long ExpectedRevision, ProofDecision Decision, string Reason, string Evidence, string EvidenceDigest,
    string Actor, string RequestFingerprint);

public sealed record ProofState(Guid ProofId, Guid ProjectId, string WorkspaceId, ProofPackageAuthority Authority,
    ProofType ProofType, string Locale, string Reviewer, Guid? SupersedesProofId,
    IReadOnlyList<ProofChecklistExecution> Executions, IReadOnlyList<ProofFinding> Findings,
    PhysicalProofReceipt? PhysicalReceipt, string? EvidenceDigest, ProofStatus Status, long Revision,
    Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record ProofSubmissionResult(ProofState State, bool Replayed);

public enum ProofType { DigitalGalley, Physical }
public enum ProofPackageAuthorityStatus { Approved, Rejected, Superseded }
public enum ProofFindingSeverity { Advisory, Major, Blocking }
public enum ProofFindingStatus { Open, Resolved }
public enum ProofReviewerDisposition { Unreviewed, Accepted, CorrectionRequired, Waived }
public enum ProofDecision { Approve, ReturnToCorrection, Reject, Supersede }
public enum ProofStatus { Draft, Evaluated, AwaitingPhysicalReceipt, Approved, CorrectionRequired, Rejected, Superseded }

public sealed class ProofValidationException : Exception { public ProofValidationException(string message) : base(message) { } }
public sealed class ProofConflictException : Exception { public ProofConflictException(string message) : base(message) { } }
public sealed class ProofTransitionException : Exception { public ProofTransitionException(string message) : base(message) { } }

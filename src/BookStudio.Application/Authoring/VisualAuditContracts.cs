namespace BookStudio.Application.Authoring;

public interface IVisualAuditCheckProvider
{
    string ProviderId { get; }
    string ProviderVersion { get; }
    IReadOnlySet<VisualAuditCheckKind> SupportedChecks { get; }

    ValueTask<IReadOnlyList<VisualAuditCheckResult>> ExecuteAsync(
        VisualAuditExecution execution,
        CancellationToken ct = default);
}

public interface IVisualAuditPolicyCatalog
{
    VisualAuditPolicy Resolve(string policyId, string policyVersion);
}

public interface IVisualAuditStore
{
    ValueTask<VisualAuditSubmissionResult> SubmitAsync(
        VisualAuditRequest request,
        DateTimeOffset at,
        CancellationToken ct = default);

    ValueTask<VisualAuditState> RecordChecksAsync(
        VisualAuditCheckBatch batch,
        DateTimeOffset at,
        CancellationToken ct = default);

    ValueTask<VisualAuditState> CompleteAsync(
        VisualAuditCompletion completion,
        DateTimeOffset at,
        CancellationToken ct = default);

    ValueTask<VisualAuditState> DecideAsync(
        VisualAuditDecisionCommand command,
        DateTimeOffset at,
        CancellationToken ct = default);

    ValueTask<VisualAuditState> ApplyWaiverAsync(
        VisualAuditWaiverCommand command,
        DateTimeOffset at,
        CancellationToken ct = default);

    ValueTask<VisualAuditState?> GetAsync(
        string workspaceId,
        Guid auditId,
        CancellationToken ct = default);
}

public sealed record VisualAuditPolicy(
    string PolicyId,
    string PolicyVersion,
    IReadOnlySet<VisualAuditCheckKind> RequiredChecks,
    IReadOnlySet<VisualAuditCheckKind> HumanReviewChecks,
    IReadOnlySet<VisualAuditFindingCode> NonWaivableFindings,
    decimal MinimumSemanticConfidence,
    TimeSpan MaximumWaiverDuration,
    string PolicyDigest);

public sealed record VisualAuditRequest(
    Guid AuditId,
    Guid ProjectId,
    string WorkspaceId,
    Guid AssetId,
    long ExpectedAssetRevision,
    string ExpectedAssetDigest,
    Guid VisualBriefId,
    long ExpectedVisualBriefRevision,
    string ExpectedVisualBriefDigest,
    Guid? AdapterRequestId,
    string? AdapterEvidenceDigest,
    string PolicyId,
    string PolicyVersion,
    IReadOnlySet<VisualAuditCheckKind> RequestedChecks,
    string Actor,
    string RequestFingerprint);

public sealed record VisualAuditExecution(
    VisualAuditRequest Request,
    VisualAuditPolicy Policy,
    Guid ExecutionId,
    DateTimeOffset StartedAtUtc);

public sealed record VisualAuditCheckResult(
    Guid CheckId,
    VisualAuditCheckKind Kind,
    VisualAuditCheckOutcome Outcome,
    VisualAuditSeverity Severity,
    decimal Confidence,
    string PolicyId,
    string PolicyVersion,
    string Evidence,
    string EvidenceDigest,
    VisualAuditFindingCode? FindingCode,
    string? RepairRecommendation,
    string ProviderId,
    string ProviderVersion,
    DateTimeOffset CompletedAtUtc);

public sealed record VisualAuditCheckBatch(
    Guid AuditId,
    string WorkspaceId,
    long ExpectedRevision,
    Guid ExecutionId,
    IReadOnlyList<VisualAuditCheckResult> Checks,
    string Actor,
    string RequestFingerprint);

public sealed record VisualAuditFinding(
    Guid FindingId,
    Guid CheckId,
    VisualAuditFindingCode Code,
    VisualAuditSeverity Severity,
    string Summary,
    string Evidence,
    string EvidenceDigest,
    bool Waivable,
    string? RepairRecommendation);

public sealed record VisualAuditCompletion(
    Guid AuditId,
    string WorkspaceId,
    long ExpectedRevision,
    VisualAuditAggregateOutcome Outcome,
    IReadOnlyList<VisualAuditFinding> Findings,
    string AggregationEvidence,
    string AggregationEvidenceDigest,
    string Actor,
    string RequestFingerprint);

public sealed record VisualAuditDecisionCommand(
    Guid RequestId,
    Guid AuditId,
    string WorkspaceId,
    long ExpectedRevision,
    VisualAuditHumanDecision Decision,
    string Authority,
    string Scope,
    string Rationale,
    string Evidence,
    string EvidenceDigest,
    string Actor,
    string RequestFingerprint);

public sealed record VisualAuditWaiverCommand(
    Guid RequestId,
    Guid AuditId,
    string WorkspaceId,
    long ExpectedRevision,
    IReadOnlySet<Guid> FindingIds,
    string Authority,
    string Scope,
    string Rationale,
    string Evidence,
    string EvidenceDigest,
    DateTimeOffset ExpiresAtUtc,
    string Actor,
    string RequestFingerprint);

public sealed record VisualAuditWaiver(
    Guid WaiverId,
    IReadOnlySet<Guid> FindingIds,
    string Authority,
    string Scope,
    string Rationale,
    string EvidenceDigest,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset CreatedAtUtc);

public sealed record VisualAuditDecision(
    Guid DecisionId,
    VisualAuditHumanDecision Decision,
    string Authority,
    string Scope,
    string Rationale,
    string EvidenceDigest,
    DateTimeOffset DecidedAtUtc);

public sealed record VisualAuditState(
    Guid AuditId,
    Guid ProjectId,
    string WorkspaceId,
    Guid AssetId,
    long ExpectedAssetRevision,
    string ExpectedAssetDigest,
    Guid VisualBriefId,
    long ExpectedVisualBriefRevision,
    string ExpectedVisualBriefDigest,
    Guid? AdapterRequestId,
    string? AdapterEvidenceDigest,
    string PolicyId,
    string PolicyVersion,
    IReadOnlySet<VisualAuditCheckKind> RequestedChecks,
    IReadOnlyList<VisualAuditCheckResult> Checks,
    IReadOnlyList<VisualAuditFinding> Findings,
    IReadOnlyList<VisualAuditWaiver> Waivers,
    IReadOnlyList<VisualAuditDecision> Decisions,
    VisualAuditAggregateOutcome Outcome,
    VisualAuditStatus Status,
    long Revision,
    Guid? MessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record VisualAuditSubmissionResult(
    VisualAuditState Audit,
    bool Replayed);

public enum VisualAuditCheckKind
{
    MediaIntegrity,
    Dimensions,
    ResolutionDpi,
    ColorProfile,
    Transparency,
    FileSize,
    Corruption,
    CropSafeZone,
    VisualBriefConformance,
    SubjectIdentity,
    Continuity,
    ProhibitedElements,
    GenreChannelFitness,
    ProvenanceCompleteness,
    RightsCompleteness,
    AccessibilityPrerequisites
}

public enum VisualAuditCheckOutcome
{
    Pass,
    Fail,
    Unknown,
    Skipped,
    Partial,
    HumanReviewRequired
}

public enum VisualAuditSeverity
{
    Information,
    Warning,
    Error,
    Blocking
}

public enum VisualAuditFindingCode
{
    StaleAuthority,
    CrossBoundaryAccess,
    AssetDigestMismatch,
    BriefDigestMismatch,
    MissingAdapterProvenance,
    InvalidMedia,
    InvalidDimensions,
    InvalidResolution,
    InvalidColorProfile,
    UnsafeTransparency,
    FileTooLarge,
    CorruptArtifact,
    CropViolation,
    SafeZoneViolation,
    BriefNonConformance,
    SubjectIdentityMismatch,
    ContinuityViolation,
    ProhibitedElement,
    ChannelMismatch,
    MissingProvenance,
    MissingRights,
    MissingAccessibility,
    IncompleteCoverage,
    UnevidencedCheck
}

public enum VisualAuditAggregateOutcome
{
    Pending,
    Pass,
    RepairRequired,
    Blocked,
    HumanReviewRequired
}

public enum VisualAuditHumanDecision
{
    Approve,
    Reject,
    ReturnToRepair,
    Escalate
}

public enum VisualAuditStatus
{
    Submitted,
    Running,
    AwaitingHumanReview,
    Completed,
    RepairRequired,
    Blocked,
    Cancelled
}

public sealed class VisualAuditValidationException : Exception
{
    public VisualAuditValidationException(string message) : base(message) { }
}

public sealed class VisualAuditConflictException : Exception
{
    public VisualAuditConflictException(string message) : base(message) { }
}

public sealed class VisualAuditTransitionException : Exception
{
    public VisualAuditTransitionException(string message) : base(message) { }
}

namespace BookStudio.Application.Authoring;

public interface ITransitionAuditStore
{
    ValueTask<TransitionAuditCreateResult> CreateAsync(TransitionAuditDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);
    ValueTask<TransitionAudit> StartAsync(TransitionAuditControlCommand command, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<TransitionAudit> AssessDimensionAsync(TransitionDimensionCommand command, DateTimeOffset assessedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<TransitionAudit> RecordFindingAsync(TransitionFindingCommand command, DateTimeOffset recordedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<TransitionAudit> DecideFindingAsync(TransitionDecisionCommand command, DateTimeOffset decidedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<TransitionAudit> ReviewAsync(TransitionAuditControlCommand command, DateTimeOffset reviewedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<TransitionAuditCloseResult> CloseAsync(TransitionAuditCloseCommand command, DateTimeOffset closedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<TransitionAudit?> GetAsync(string workspaceId, Guid auditId, CancellationToken cancellationToken = default);
}

public sealed record TransitionAuditDraft(Guid AuditId, Guid ProjectId, string WorkspaceId, TransitionScope Scope, TransitionEndpoint Source, TransitionEndpoint Target, string RuleSetVersion, string Actor, string RequestFingerprint);
public sealed record TransitionEndpoint(string ArtifactType, Guid ArtifactId, long Version, string ContentDigest, string StateJson);
public sealed record TransitionAuditControlCommand(Guid RequestId, string WorkspaceId, Guid AuditId, long ExpectedRevision, string Actor, string RequestFingerprint);
public sealed record TransitionDimensionCommand(Guid RequestId, string WorkspaceId, Guid AuditId, long ExpectedRevision, TransitionDimension Dimension, TransitionAssessmentStatus Status, string Evidence, string Actor, string RequestFingerprint);
public sealed record TransitionFindingCommand(Guid RequestId, string WorkspaceId, Guid AuditId, long ExpectedRevision, Guid FindingId, string RuleId, string RuleVersion, TransitionSeverity Severity, string Evidence, string Recommendation, string Actor, string RequestFingerprint);
public sealed record TransitionDecisionCommand(Guid RequestId, string WorkspaceId, Guid AuditId, long ExpectedRevision, Guid FindingId, TransitionDecision Decision, string Reason, string Actor, string RequestFingerprint);
public sealed record TransitionAuditCloseCommand(Guid RequestId, string WorkspaceId, Guid AuditId, long ExpectedRevision, string Actor, string Reason, string RequestFingerprint);
public sealed record TransitionDimensionAssessment(TransitionDimension Dimension, TransitionAssessmentStatus Status, string Evidence, string Actor, DateTimeOffset AssessedAtUtc);
public sealed record TransitionFinding(Guid FindingId, string RuleId, string RuleVersion, TransitionSeverity Severity, string Evidence, string Recommendation, TransitionDecision Decision, string? DecisionReason, string Actor, DateTimeOffset CreatedAtUtc, DateTimeOffset? DecidedAtUtc);
public sealed record TransitionAudit(Guid AuditId, Guid ProjectId, string WorkspaceId, TransitionScope Scope, TransitionEndpoint Source, TransitionEndpoint Target, string RuleSetVersion, IReadOnlyList<TransitionDimensionAssessment> Assessments, IReadOnlyList<TransitionFinding> Findings, long Revision, TransitionAuditStatus Status, Guid? ClosedMessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record TransitionAuditCreateResult(TransitionAudit Audit, bool Replayed);
public sealed record TransitionAuditCloseResult(TransitionAudit Audit, bool Replayed, Guid MessageId);

public enum TransitionScope { Paragraph, Scene, Chapter }
public enum TransitionDimension { Time, Location, Characters, Objects, Knowledge, Objective, Tone, Causality }
public enum TransitionAssessmentStatus { Supported, Partial, Broken, NotApplicable }
public enum TransitionSeverity { Informational, Warning, Blocking }
public enum TransitionDecision { Open, Accepted, Resolved, Dismissed }
public enum TransitionAuditStatus { Draft, Running, Reviewed, Closed }
public sealed class TransitionAuditValidationException : Exception { public TransitionAuditValidationException(string message) : base(message) { } }
public sealed class TransitionAuditConflictException : Exception { public TransitionAuditConflictException(string message) : base(message) { } }
public sealed class TransitionAuditTransitionException : Exception { public TransitionAuditTransitionException(string message) : base(message) { } }

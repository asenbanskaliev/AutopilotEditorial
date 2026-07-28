namespace BookStudio.Application.Authoring;

public interface ISceneCoherenceStore
{
    ValueTask<SceneCoherenceCreateResult> CreateAsync(SceneCoherenceDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);
    ValueTask<SceneCoherenceAudit> StartAsync(SceneCoherenceControlCommand command, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<SceneCoherenceAudit> AssessBeatAsync(SceneBeatAssessmentCommand command, DateTimeOffset assessedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<SceneCoherenceAudit> RecordCausalLinkAsync(SceneCausalLinkCommand command, DateTimeOffset recordedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<SceneCoherenceAudit> RecordFindingAsync(SceneCoherenceFindingCommand command, DateTimeOffset recordedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<SceneCoherenceAudit> DecideFindingAsync(SceneCoherenceDecisionCommand command, DateTimeOffset decidedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<SceneCoherenceAudit> ReviewAsync(SceneCoherenceControlCommand command, DateTimeOffset reviewedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<SceneCoherenceCloseResult> CloseAsync(SceneCoherenceCloseCommand command, DateTimeOffset closedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<SceneCoherenceAudit?> GetAsync(string workspaceId, Guid auditId, CancellationToken cancellationToken = default);
}

public sealed record SceneCoherenceDraft(Guid AuditId, Guid ProjectId, Guid GenerationId, Guid SceneApprovalMessageId, string SceneContentDigest, Guid ScenePlanId, long ScenePlanVersion, string SceneKey, string WorkspaceId, string RuleSetVersion, string SourceText, string Actor, string RequestFingerprint);
public sealed record SceneCoherenceControlCommand(Guid RequestId, string WorkspaceId, Guid AuditId, long ExpectedRevision, string Actor, string RequestFingerprint);
public sealed record SceneBeatAssessmentCommand(Guid RequestId, string WorkspaceId, Guid AuditId, long ExpectedRevision, string BeatKey, int PlannedOrder, SceneBeatStatus Status, int? StartOffset, int? Length, string Evidence, string Actor, string RequestFingerprint);
public sealed record SceneCausalLinkCommand(Guid RequestId, string WorkspaceId, Guid AuditId, long ExpectedRevision, Guid LinkId, int CauseStartOffset, int CauseLength, int EffectStartOffset, int EffectLength, SceneCausalStatus Status, string Evidence, string Actor, string RequestFingerprint);
public sealed record SceneCoherenceFindingCommand(Guid RequestId, string WorkspaceId, Guid AuditId, long ExpectedRevision, Guid FindingId, string RuleId, string RuleVersion, SceneCoherenceFindingCategory Category, SceneCoherenceSeverity Severity, int? StartOffset, int? Length, string Evidence, string Recommendation, string Actor, string RequestFingerprint);
public sealed record SceneCoherenceDecisionCommand(Guid RequestId, string WorkspaceId, Guid AuditId, long ExpectedRevision, Guid FindingId, SceneCoherenceDecision Decision, string Reason, string Actor, string RequestFingerprint);
public sealed record SceneCoherenceCloseCommand(Guid RequestId, string WorkspaceId, Guid AuditId, long ExpectedRevision, string Actor, string Reason, string RequestFingerprint);

public sealed record SceneBeatAssessment(string BeatKey, int PlannedOrder, SceneBeatStatus Status, int? StartOffset, int? Length, string Evidence, string Actor, DateTimeOffset AssessedAtUtc);
public sealed record SceneCausalLink(Guid LinkId, int CauseStartOffset, int CauseLength, int EffectStartOffset, int EffectLength, SceneCausalStatus Status, string Evidence, string Actor, DateTimeOffset RecordedAtUtc);
public sealed record SceneCoherenceFinding(Guid FindingId, string RuleId, string RuleVersion, SceneCoherenceFindingCategory Category, SceneCoherenceSeverity Severity, int? StartOffset, int? Length, string Evidence, string Recommendation, SceneCoherenceDecision Decision, string? DecisionReason, string Actor, DateTimeOffset CreatedAtUtc, DateTimeOffset? DecidedAtUtc);
public sealed record SceneCoherenceAudit(Guid AuditId, Guid ProjectId, Guid GenerationId, Guid SceneApprovalMessageId, string SceneContentDigest, Guid ScenePlanId, long ScenePlanVersion, string SceneKey, string WorkspaceId, string RuleSetVersion, string SourceText, string EntryState, string ExitState, IReadOnlyList<string> PlannedBeats, IReadOnlyList<SceneBeatAssessment> BeatAssessments, IReadOnlyList<SceneCausalLink> CausalLinks, IReadOnlyList<SceneCoherenceFinding> Findings, long Revision, SceneCoherenceStatus Status, Guid? ClosedMessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record SceneCoherenceCreateResult(SceneCoherenceAudit Audit, bool Replayed);
public sealed record SceneCoherenceCloseResult(SceneCoherenceAudit Audit, bool Replayed, Guid MessageId);

public enum SceneCoherenceStatus { Draft, Running, Reviewed, Closed }
public enum SceneBeatStatus { Satisfied, Partial, Missing, OutOfOrder }
public enum SceneCausalStatus { Supported, Broken, Reversed, Unsupported }
public enum SceneCoherenceFindingCategory { BeatCoverage, Causality, EntryState, ExitState, Objective }
public enum SceneCoherenceSeverity { Informational, Warning, Blocking }
public enum SceneCoherenceDecision { Open, Accepted, Resolved, Dismissed }
public sealed class SceneCoherenceValidationException : Exception { public SceneCoherenceValidationException(string message) : base(message) { } }
public sealed class SceneCoherenceConflictException : Exception { public SceneCoherenceConflictException(string message) : base(message) { } }
public sealed class SceneCoherenceTransitionException : Exception { public SceneCoherenceTransitionException(string message) : base(message) { } }

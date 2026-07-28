namespace BookStudio.Application.Authoring;

public interface IParagraphCoherenceStore
{
    ValueTask<ParagraphCoherenceCreateResult> CreateAsync(ParagraphCoherenceDraft draft, DateTimeOffset at, CancellationToken cancellationToken = default);
    ValueTask<ParagraphCoherenceAudit> StartAsync(ParagraphCoherenceCommand command, DateTimeOffset at, CancellationToken cancellationToken = default);
    ValueTask<ParagraphCoherenceAudit> RecordFindingAsync(ParagraphFindingCommand command, DateTimeOffset at, CancellationToken cancellationToken = default);
    ValueTask<ParagraphCoherenceAudit> DecideFindingAsync(ParagraphFindingDecisionCommand command, DateTimeOffset at, CancellationToken cancellationToken = default);
    ValueTask<ParagraphCoherenceAudit> ReviewAsync(ParagraphCoherenceCommand command, DateTimeOffset at, CancellationToken cancellationToken = default);
    ValueTask<ParagraphCoherenceCloseResult> CloseAsync(ParagraphCoherenceCloseCommand command, DateTimeOffset at, CancellationToken cancellationToken = default);
    ValueTask<ParagraphCoherenceAudit?> GetAsync(string workspaceId, Guid auditId, CancellationToken cancellationToken = default);
}

public sealed record ParagraphCoherenceDraft(Guid AuditId, Guid ProjectId, Guid GenerationId, Guid SceneApprovalMessageId, string SceneContentDigest, string WorkspaceId, string RuleSetVersion, string SourceText, string Actor, string RequestFingerprint);
public sealed record ParagraphCoherenceCommand(Guid RequestId, string WorkspaceId, Guid AuditId, long ExpectedRevision, string Actor, string RequestFingerprint);
public sealed record ParagraphFindingCommand(Guid RequestId, string WorkspaceId, Guid AuditId, long ExpectedRevision, Guid FindingId, string RuleId, string RuleVersion, ParagraphFindingCategory Category, ParagraphFindingSeverity Severity, int ParagraphOrdinal, int StartOffset, int Length, string Evidence, string Recommendation, string Actor, string RequestFingerprint);
public sealed record ParagraphFindingDecisionCommand(Guid RequestId, string WorkspaceId, Guid AuditId, long ExpectedRevision, Guid FindingId, ParagraphFindingDecision Decision, string Reason, string Actor, string RequestFingerprint);
public sealed record ParagraphCoherenceCloseCommand(Guid RequestId, string WorkspaceId, Guid AuditId, long ExpectedRevision, string Actor, string Reason, string RequestFingerprint);
public sealed record ParagraphSegment(int Ordinal, int StartOffset, int Length, string Text);
public sealed record ParagraphFinding(Guid FindingId, string RuleId, string RuleVersion, ParagraphFindingCategory Category, ParagraphFindingSeverity Severity, int ParagraphOrdinal, int StartOffset, int Length, string Evidence, string Recommendation, ParagraphFindingDecision Decision, string? DecisionReason, string Actor, DateTimeOffset CreatedAtUtc, DateTimeOffset? DecidedAtUtc);
public sealed record ParagraphCoherenceAudit(Guid AuditId, Guid ProjectId, Guid GenerationId, Guid SceneApprovalMessageId, string SceneContentDigest, string WorkspaceId, string RuleSetVersion, string SourceText, IReadOnlyList<ParagraphSegment> Paragraphs, IReadOnlyList<ParagraphFinding> Findings, long Revision, ParagraphCoherenceStatus Status, Guid? ClosedMessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record ParagraphCoherenceCreateResult(ParagraphCoherenceAudit Audit, bool Replayed);
public sealed record ParagraphCoherenceCloseResult(ParagraphCoherenceAudit Audit, bool Replayed, Guid MessageId);
public enum ParagraphCoherenceStatus { Draft, Running, Reviewed, Closed }
public enum ParagraphFindingCategory { Continuity, Reference, Repetition, Contradiction, Clarity, Flow }
public enum ParagraphFindingSeverity { Info, Warning, Blocking }
public enum ParagraphFindingDecision { Open, Accepted, Resolved, Dismissed }
public sealed class ParagraphCoherenceConflictException : Exception { public ParagraphCoherenceConflictException(string message) : base(message) { } }
public sealed class ParagraphCoherenceTransitionException : Exception { public ParagraphCoherenceTransitionException(string message) : base(message) { } }
public sealed class ParagraphCoherenceValidationException : Exception { public ParagraphCoherenceValidationException(string message) : base(message) { } }
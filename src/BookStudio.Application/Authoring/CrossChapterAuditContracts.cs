namespace BookStudio.Application.Authoring;

public interface ICrossChapterAuditStore
{
    ValueTask<CrossChapterAuditCreateResult> CreateAsync(CrossChapterAuditDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<CrossChapterAudit> EvaluateAsync(CrossChapterAuditControlCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<CrossChapterAudit> DecideAsync(CrossChapterAuditDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<CrossChapterAudit> ReopenAsync(CrossChapterAuditReopenCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<CrossChapterAudit?> GetAsync(string workspaceId, Guid auditId, CancellationToken ct = default);
}

public sealed record CrossChapterSnapshotItem(string ChapterId, Guid GateId, int LockedVersion, string LockedDigest, Guid MemoryCommitId, string MemoryDigest);
public sealed record CrossChapterAuditDraft(Guid AuditId, Guid ProjectId, string WorkspaceId, string RuleSet, IReadOnlyList<CrossChapterSnapshotItem> Chapters, string Actor, string Evidence, string RequestFingerprint);
public sealed record CrossChapterAuditControlCommand(Guid RequestId, string WorkspaceId, Guid AuditId, long ExpectedRevision, string Actor, string RequestFingerprint);
public sealed record CrossChapterAuditDecisionCommand(Guid RequestId, string WorkspaceId, Guid AuditId, long ExpectedRevision, CrossChapterAuditDecision Decision, string Reason, string Actor, string RequestFingerprint);
public sealed record CrossChapterAuditReopenCommand(Guid RequestId, string WorkspaceId, Guid AuditId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);
public sealed record CrossChapterAuditFinding(Guid FindingId, string Rule, CrossChapterAuditSeverity Severity, IReadOnlyList<string> ChapterIds, string Scope, string Evidence, bool Open);
public sealed record CrossChapterAudit(Guid AuditId, Guid ProjectId, string WorkspaceId, string RuleSet, IReadOnlyList<CrossChapterSnapshotItem> Chapters, IReadOnlyList<CrossChapterAuditFinding> Findings, string Actor, string Evidence, string PayloadHash, long Revision, CrossChapterAuditStatus Status, CrossChapterAuditDecision? Decision, string? DecisionReason, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record CrossChapterAuditCreateResult(CrossChapterAudit Audit, bool Replayed);
public enum CrossChapterAuditSeverity { Info, Warning, Blocking }
public enum CrossChapterAuditStatus { Proposed, Evaluated, Approved, Rejected, RepairRequired, Reopened, Stale }
public enum CrossChapterAuditDecision { Approve, Reject, Repair }
public sealed class CrossChapterAuditValidationException : Exception { public CrossChapterAuditValidationException(string message) : base(message) { } }
public sealed class CrossChapterAuditConflictException : Exception { public CrossChapterAuditConflictException(string message) : base(message) { } }
public sealed class CrossChapterAuditTransitionException : Exception { public CrossChapterAuditTransitionException(string message) : base(message) { } }

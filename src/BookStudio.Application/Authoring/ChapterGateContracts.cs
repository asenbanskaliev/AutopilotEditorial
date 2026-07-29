namespace BookStudio.Application.Authoring;

public interface IChapterGateStore
{
    ValueTask<ChapterGateCreateResult> CreateAsync(ChapterGateDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ChapterGate> EvaluateAsync(ChapterGateControlCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ChapterGate> DecideAsync(ChapterGateDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ChapterGate> ReopenAsync(ChapterGateReopenCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<ChapterGate?> GetAsync(string workspaceId, Guid gateId, CancellationToken ct = default);
}

public sealed record ChapterGateDraft(Guid GateId, Guid ProjectId, string WorkspaceId, string ChapterId, int ExpectedVersion, string ExpectedDigest, string Actor, string RequestFingerprint);
public sealed record ChapterGateControlCommand(Guid RequestId, string WorkspaceId, Guid GateId, long ExpectedRevision, string Actor, string RequestFingerprint);
public sealed record ChapterGateDecisionCommand(Guid RequestId, string WorkspaceId, Guid GateId, long ExpectedRevision, ChapterGateDecision Decision, string Reason, string Actor, string RequestFingerprint);
public sealed record ChapterGateReopenCommand(Guid RequestId, string WorkspaceId, Guid GateId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);
public sealed record ChapterGateFinding(Guid FindingId, string Code, string Message, bool Blocking, string Source);
public sealed record ChapterGate(Guid GateId, Guid ProjectId, string WorkspaceId, string ChapterId, int ExpectedVersion, string ExpectedDigest, IReadOnlyList<ChapterGateFinding> Findings, string Actor, long Revision, ChapterGateStatus Status, ChapterGateDecision? Decision, string? DecisionReason, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record ChapterGateCreateResult(ChapterGate Gate, bool Replayed);
public enum ChapterGateStatus { Proposed, Evaluated, Locked, Rejected, RepairRequired, Reopened }
public enum ChapterGateDecision { Approve, Reject, Repair }
public sealed class ChapterGateValidationException : Exception { public ChapterGateValidationException(string message) : base(message) { } }
public sealed class ChapterGateConflictException : Exception { public ChapterGateConflictException(string message) : base(message) { } }
public sealed class ChapterGateTransitionException : Exception { public ChapterGateTransitionException(string message) : base(message) { } }
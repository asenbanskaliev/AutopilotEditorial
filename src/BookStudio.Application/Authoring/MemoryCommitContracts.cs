namespace BookStudio.Application.Authoring;

public interface IMemoryCommitStore
{
    ValueTask<MemoryDeltaCreateResult> ProposeAsync(MemoryDeltaDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<MemoryDelta> ValidateAsync(MemoryDeltaControlCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<MemoryDelta> CommitAsync(MemoryDeltaControlCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<MemoryDelta> RejectAsync(MemoryDeltaDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<MemoryDelta?> GetAsync(string workspaceId, Guid deltaId, CancellationToken ct = default);
}

public sealed record MemoryDeltaEntry(string Projection, string EntityId, string Operation, string ExpectedDigest, string PayloadJson);
public sealed record MemoryDeltaDraft(Guid DeltaId, Guid ProjectId, string WorkspaceId, string ChapterId, Guid GateId, int LockedVersion, string LockedDigest, IReadOnlyList<MemoryDeltaEntry> Entries, string Evidence, string Actor, string RequestFingerprint);
public sealed record MemoryDeltaControlCommand(Guid RequestId, string WorkspaceId, Guid DeltaId, long ExpectedRevision, string Actor, string RequestFingerprint);
public sealed record MemoryDeltaDecisionCommand(Guid RequestId, string WorkspaceId, Guid DeltaId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);
public sealed record MemoryDelta(Guid DeltaId, Guid ProjectId, string WorkspaceId, string ChapterId, Guid GateId, int LockedVersion, string LockedDigest, IReadOnlyList<MemoryDeltaEntry> Entries, string Evidence, string Actor, string PayloadHash, long Revision, MemoryDeltaStatus Status, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record MemoryDeltaCreateResult(MemoryDelta Delta, bool Replayed);
public enum MemoryDeltaStatus { Proposed, Validated, Committed, Rejected, Stale }
public sealed class MemoryDeltaValidationException : Exception { public MemoryDeltaValidationException(string message) : base(message) { } }
public sealed class MemoryDeltaConflictException : Exception { public MemoryDeltaConflictException(string message) : base(message) { } }
public sealed class MemoryDeltaTransitionException : Exception { public MemoryDeltaTransitionException(string message) : base(message) { } }
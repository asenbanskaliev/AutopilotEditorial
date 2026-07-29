namespace BookStudio.Application.Authoring;

public interface IRepairPatchStore
{
    ValueTask<RepairPatchCreateResult> ProposeAsync(RepairPatchDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<RepairPatch> ValidateAsync(RepairPatchControlCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<RepairPatch> ApplyAsync(RepairPatchControlCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<RepairPatch> RejectAsync(RepairPatchDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<RepairPatch?> GetAsync(string workspaceId, Guid patchId, CancellationToken ct = default);
}

public sealed record RepairOperation(string Path, RepairOperationKind Kind, string? ExpectedValue, string? NewValue);
public sealed record RepairPatchDraft(Guid PatchId, Guid ProjectId, string WorkspaceId, string ArtifactId, int ExpectedVersion, string ExpectedDigest, string Scope, IReadOnlyList<RepairOperation> Operations, string Reason, string Evidence, string AuthorityType, Guid AuthorityId, string Actor, string RequestFingerprint);
public sealed record RepairPatchControlCommand(Guid RequestId, string WorkspaceId, Guid PatchId, long ExpectedRevision, string Actor, string RequestFingerprint);
public sealed record RepairPatchDecisionCommand(Guid RequestId, string WorkspaceId, Guid PatchId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);
public sealed record RepairPatch(Guid PatchId, Guid ProjectId, string WorkspaceId, string ArtifactId, int ExpectedVersion, string ExpectedDigest, string Scope, IReadOnlyList<RepairOperation> Operations, string Reason, string Evidence, string AuthorityType, Guid AuthorityId, string Actor, string PayloadHash, long Revision, RepairPatchStatus Status, string? ResultDigest, int? ResultVersion, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record RepairPatchCreateResult(RepairPatch Patch, bool Replayed);
public enum RepairOperationKind { ReplaceValue, AddValue, RemoveValue }
public enum RepairPatchStatus { Proposed, Validated, Applied, Rejected, Stale }
public sealed class RepairPatchValidationException : Exception { public RepairPatchValidationException(string message) : base(message) { } }
public sealed class RepairPatchConflictException : Exception { public RepairPatchConflictException(string message) : base(message) { } }
public sealed class RepairPatchTransitionException : Exception { public RepairPatchTransitionException(string message) : base(message) { } }

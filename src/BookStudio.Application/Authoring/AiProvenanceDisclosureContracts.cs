namespace BookStudio.Application.Authoring;

public interface IAiProvenanceDisclosureStore
{
    ValueTask<AiProvenanceCreateResult> CreateAsync(AiProvenanceDraft draft, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<AiProvenanceRecord> EvaluateAsync(AiProvenanceEvaluateCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<AiProvenanceRecord> DecideAsync(AiProvenanceDecisionCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<AiProvenanceRecord> ReopenAsync(AiProvenanceReopenCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<AiProvenanceRecord> MarkStaleAsync(AiProvenanceStaleCommand command, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<AiProvenanceRecord?> GetAsync(string workspaceId, Guid recordId, CancellationToken ct = default);
}

public sealed record AiProvenanceDraft(Guid RecordId, Guid ProjectId, string WorkspaceId, Guid RightsCaseId, long ExpectedRightsRevision, string ExpectedRightsDigest, Guid AssetId, string AssetDigest, int AssetVersion, string Actor, string SnapshotJson, string RequestFingerprint);
public sealed record AiProvenanceEvaluateCommand(Guid RequestId, string WorkspaceId, Guid RecordId, long ExpectedRevision, AiProvenanceClassification Classification, string? Provider, string? Model, string? ModelVersion, DateTimeOffset? GeneratedAtUtc, string? PromptReference, IReadOnlyList<string> HumanTransformations, string DeclaredScope, string Evidence, IReadOnlyList<AiDisclosureDraft> Disclosures, string PolicyVersion, string Actor, string RequestFingerprint);
public sealed record AiProvenanceDecisionCommand(Guid RequestId, string WorkspaceId, Guid RecordId, long ExpectedRevision, AiProvenanceDecision Decision, string Reason, string Actor, string RequestFingerprint);
public sealed record AiProvenanceReopenCommand(Guid RequestId, string WorkspaceId, Guid RecordId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);
public sealed record AiProvenanceStaleCommand(Guid RequestId, string WorkspaceId, Guid RecordId, long ExpectedRevision, string Reason, string Actor, string RequestFingerprint);

public sealed record AiDisclosureDraft(string Channel, string Locale, string Format, string Text, string PolicyVersion);
public sealed record AiDisclosure(string Channel, string Locale, string Format, string Text, string PolicyVersion);

public sealed record AiProvenanceRecord(Guid RecordId, Guid ProjectId, string WorkspaceId, Guid RightsCaseId, long ExpectedRightsRevision, string ExpectedRightsDigest, Guid AssetId, string AssetDigest, int AssetVersion, string Actor, string SnapshotJson, long Revision, AiProvenanceStatus Status, AiProvenanceClassification? Classification, string? Provider, string? Model, string? ModelVersion, DateTimeOffset? GeneratedAtUtc, string? PromptReference, IReadOnlyList<string> HumanTransformations, string? DeclaredScope, string? Evidence, IReadOnlyList<AiDisclosure> Disclosures, string? PolicyVersion, AiProvenanceDecision? Decision, string? DecisionReason, Guid? MessageId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record AiProvenanceCreateResult(AiProvenanceRecord Record, bool Replayed);

public enum AiProvenanceClassification { HumanCreated, AiAssisted, AiGenerated, Mixed, Unknown }
public enum AiProvenanceStatus { Proposed, Evaluated, Approved, Rejected, RepairRequired, Revoked, Stale }
public enum AiProvenanceDecision { Approve, Reject, ReturnToRepair, Revoke }

public sealed class AiProvenanceValidationException : Exception { public AiProvenanceValidationException(string message) : base(message) { } }
public sealed class AiProvenanceConflictException : Exception { public AiProvenanceConflictException(string message) : base(message) { } }
public sealed class AiProvenanceTransitionException : Exception { public AiProvenanceTransitionException(string message) : base(message) { } }

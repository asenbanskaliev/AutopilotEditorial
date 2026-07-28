namespace BookStudio.Application.Authoring;

public interface IEditorialProposalStore
{
    ValueTask<EditorialProposalCreateResult> CreateAsync(EditorialProposalDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);
    ValueTask<EditorialProposal> ReviseAsync(EditorialProposalRevisionCommand command, DateTimeOffset revisedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<EditorialProposal> SubmitAsync(EditorialProposalSubmitCommand command, DateTimeOffset submittedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<EditorialProposalDecisionResult> DecideAsync(EditorialProposalDecisionCommand command, DateTimeOffset decidedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<EditorialProposal?> GetAsync(string workspaceId, Guid proposalId, CancellationToken cancellationToken = default);
}

public sealed record EditorialProposalDraft(
    Guid ProposalId,
    Guid ProjectId,
    Guid DiscoverySessionId,
    string WorkspaceId,
    string SchemaVersion,
    EditorialProposalContent Content,
    IReadOnlyList<ProposalEvidenceReference> Evidence,
    string Actor,
    string RequestFingerprint);

public sealed record EditorialProposalRevisionCommand(
    Guid RequestId,
    string WorkspaceId,
    Guid ProposalId,
    long ExpectedRevision,
    EditorialProposalContent Content,
    IReadOnlyList<ProposalEvidenceReference> Evidence,
    string Actor,
    string Reason,
    string RequestFingerprint);

public sealed record EditorialProposalSubmitCommand(Guid RequestId, string WorkspaceId, Guid ProposalId, long ExpectedRevision, string Actor, string RequestFingerprint);

public sealed record EditorialProposalDecisionCommand(
    Guid RequestId,
    string WorkspaceId,
    Guid ProposalId,
    long ExpectedRevision,
    EditorialProposalDecision Decision,
    string Actor,
    string Reason,
    string RequestFingerprint);

public sealed record EditorialProposalContent(
    string Premise,
    string Audience,
    string Promise,
    string Scope,
    string Differentiators,
    string Risks,
    string Assumptions,
    string SuccessCriteria,
    string RecommendedNextStep);

public sealed record ProposalEvidenceReference(string Kind, string Key, string Reference);

public sealed record EditorialProposal(
    Guid ProposalId,
    Guid ProjectId,
    Guid DiscoverySessionId,
    string WorkspaceId,
    string SchemaVersion,
    EditorialProposalStatus Status,
    long Revision,
    EditorialProposalContent Content,
    IReadOnlyList<ProposalEvidenceReference> Evidence,
    string? DecisionActor,
    string? DecisionReason,
    Guid? ApprovalMessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record EditorialProposalCreateResult(EditorialProposal Proposal, bool Replayed);
public sealed record EditorialProposalDecisionResult(EditorialProposal Proposal, bool Replayed, Guid? ApprovalMessageId);

public enum EditorialProposalStatus { Draft, Submitted, Approved, Rejected }
public enum EditorialProposalDecision { Approve, Reject }

public sealed class EditorialProposalConflictException : Exception { public EditorialProposalConflictException(string message) : base(message) { } }
public sealed class EditorialProposalTransitionException : Exception { public EditorialProposalTransitionException(string message) : base(message) { } }
public sealed class EditorialProposalValidationException : Exception { public EditorialProposalValidationException(string message) : base(message) { } }

namespace BookStudio.Application.Autopilot;

public interface IHumanGateStore
{
    ValueTask<HumanGateCreateResult> CreateAsync(HumanGateDraft draft, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);
    ValueTask<HumanGateRequest?> GetAsync(Guid requestId, CancellationToken cancellationToken = default);
    ValueTask<HumanGateRequest> ClaimAsync(Guid requestId, string actorId, TimeSpan leaseDuration, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    ValueTask<HumanGateDecisionResult> DecideAsync(Guid requestId, string actorId, HumanGateDecision decision, string note, DateTimeOffset decidedAtUtc, CancellationToken cancellationToken = default);
    ValueTask<HumanGateRequest> CancelAsync(Guid requestId, string actorId, DateTimeOffset cancelledAtUtc, CancellationToken cancellationToken = default);
    ValueTask<int> ExpireDueAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}

public sealed record HumanGateDraft(
    Guid RequestId,
    string WorkflowId,
    string WorkflowVersion,
    string StepId,
    Guid JobId,
    string Prompt,
    string SchemaVersion,
    DateTimeOffset ExpiresAtUtc);

public sealed record HumanGateRequest(
    Guid RequestId,
    string WorkflowId,
    string WorkflowVersion,
    string StepId,
    Guid JobId,
    string Prompt,
    string SchemaVersion,
    DateTimeOffset ExpiresAtUtc,
    HumanGateStatus Status,
    string? ClaimedBy,
    DateTimeOffset? ClaimUntilUtc,
    HumanGateDecision? Decision,
    string? DecisionNote,
    string? DecidedBy,
    DateTimeOffset? DecidedAtUtc,
    Guid? ResumeMessageId,
    DateTimeOffset CreatedAtUtc);

public enum HumanGateStatus { Open, Claimed, Approved, Rejected, Expired, Cancelled }
public enum HumanGateDecision { Approve, Reject }
public enum HumanGateCreateResult { Inserted, AlreadyExists }

public sealed record HumanGateDecisionResult(HumanGateRequest Request, bool Replayed);

public sealed class HumanGateConflictException : Exception
{
    public HumanGateConflictException(Guid requestId, string message) : base(message) => RequestId = requestId;
    public Guid RequestId { get; }
}

public sealed class HumanGateLeaseException : Exception
{
    public HumanGateLeaseException(Guid requestId, string actorId) : base($"Actor '{actorId}' does not own a live claim for gate '{requestId:D}'.")
    { RequestId = requestId; ActorId = actorId; }
    public Guid RequestId { get; }
    public string ActorId { get; }
}

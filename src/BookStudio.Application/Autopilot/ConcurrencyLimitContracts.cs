namespace BookStudio.Application.Autopilot;

public interface IConcurrencyLimitStore
{
    ValueTask<ConcurrencyLimitDefinition> UpsertLimitAsync(
        ConcurrencyLimitDefinition definition,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<ConcurrencyAcquireResult> AcquireAsync(
        ConcurrencyAcquireCommand command,
        DateTimeOffset acquiredAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<ConcurrencyGrant> RenewAsync(
        ConcurrencyRenewCommand command,
        DateTimeOffset renewedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<ConcurrencyReleaseResult> ReleaseAsync(
        ConcurrencyReleaseCommand command,
        DateTimeOffset releasedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<int> ReclaimExpiredAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    ValueTask<ConcurrencyGrant?> GetGrantAsync(
        Guid grantId,
        CancellationToken cancellationToken = default);
}

public sealed record ConcurrencyLimitDefinition(
    ConcurrencyScopeType ScopeType,
    string ScopeKey,
    int Capacity,
    long Version,
    string UpdatedBy);

public sealed record ConcurrencyScopeRequest(
    ConcurrencyScopeType ScopeType,
    string ScopeKey,
    int Units);

public sealed record ConcurrencyAcquireCommand(
    Guid RequestId,
    string OwnerId,
    int Priority,
    TimeSpan LeaseDuration,
    IReadOnlyList<ConcurrencyScopeRequest> Scopes,
    string RequestFingerprint);

public sealed record ConcurrencyRenewCommand(
    Guid RequestId,
    Guid GrantId,
    long Generation,
    string OwnerId,
    TimeSpan LeaseDuration,
    string RequestFingerprint);

public sealed record ConcurrencyReleaseCommand(
    Guid RequestId,
    Guid GrantId,
    long Generation,
    string OwnerId,
    string RequestFingerprint);

public sealed record ConcurrencyGrant(
    Guid GrantId,
    Guid AcquireRequestId,
    string OwnerId,
    int Priority,
    long Generation,
    ConcurrencyGrantStatus Status,
    IReadOnlyList<ConcurrencyScopeRequest> Scopes,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset LeaseUntilUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ConcurrencyAcquireResult(
    ConcurrencyAcquireOutcome Outcome,
    ConcurrencyGrant? Grant,
    bool Replayed,
    IReadOnlyList<ConcurrencyScopeAvailability> Availability);

public sealed record ConcurrencyScopeAvailability(
    ConcurrencyScopeType ScopeType,
    string ScopeKey,
    int Capacity,
    int Used,
    int Requested);

public sealed record ConcurrencyReleaseResult(ConcurrencyGrant Grant, bool Replayed);

public enum ConcurrencyScopeType
{
    Global,
    Provider,
    ModelRole,
    Workflow,
    Project,
    ToolProfile,
}

public enum ConcurrencyGrantStatus
{
    Active,
    Released,
    Expired,
}

public enum ConcurrencyAcquireOutcome
{
    Granted,
    CapacityUnavailable,
}

public sealed class ConcurrencyLimitConflictException : Exception
{
    public ConcurrencyLimitConflictException(string message) : base(message) { }
}

public sealed class ConcurrencyLeaseException : Exception
{
    public ConcurrencyLeaseException(Guid grantId, string ownerId)
        : base($"Owner '{ownerId}' does not hold the live lease for grant '{grantId:D}'.")
    {
        GrantId = grantId;
        OwnerId = ownerId;
    }

    public Guid GrantId { get; }
    public string OwnerId { get; }
}

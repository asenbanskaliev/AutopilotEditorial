namespace BookStudio.Application.Autopilot;

public interface IDeadLetterStore
{
    ValueTask<DeadLetterCaptureResult> CaptureAsync(
        DeadLetterDraft draft,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<DeadLetterRepairResult> RepairAsync(
        DeadLetterRepairCommand command,
        DateTimeOffset repairedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<DeadLetterRecoveryResult> RequeueAsync(
        DeadLetterRecoveryCommand command,
        DateTimeOffset requeuedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<DeadLetterRecord> DiscardAsync(
        DeadLetterDiscardCommand command,
        DateTimeOffset discardedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<DeadLetterRecord?> GetAsync(
        Guid deadLetterId,
        CancellationToken cancellationToken = default);
}

public sealed record DeadLetterDraft(
    Guid DeadLetterId,
    DeadLetterSourceKind SourceKind,
    Guid SourceId,
    string EventType,
    string SchemaVersion,
    string PayloadJson,
    int AttemptCount,
    DeadLetterFailureClass FailureClass,
    string Error,
    string FailureFingerprint);

public sealed record DeadLetterRepairCommand(
    Guid RequestId,
    Guid DeadLetterId,
    string Actor,
    string Reason,
    string ReplacementPayloadJson,
    string ReplacementSchemaVersion,
    string RequestFingerprint);

public sealed record DeadLetterRecoveryCommand(
    Guid RequestId,
    Guid DeadLetterId,
    string Actor,
    string Reason,
    string RequestFingerprint);

public sealed record DeadLetterDiscardCommand(
    Guid RequestId,
    Guid DeadLetterId,
    string Actor,
    string Reason,
    string RequestFingerprint);

public sealed record DeadLetterRecord(
    Guid DeadLetterId,
    DeadLetterSourceKind SourceKind,
    Guid SourceId,
    string EventType,
    string OriginalSchemaVersion,
    string OriginalPayloadJson,
    int AttemptCount,
    DeadLetterFailureClass FailureClass,
    string Error,
    string FailureFingerprint,
    DeadLetterStatus Status,
    string? ReplacementSchemaVersion,
    string? ReplacementPayloadJson,
    string? LastActor,
    string? LastReason,
    Guid? RecoveryMessageId,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record DeadLetterCaptureResult(DeadLetterRecord Record, bool AlreadyExists);
public sealed record DeadLetterRepairResult(DeadLetterRecord Record, bool Replayed);
public sealed record DeadLetterRecoveryResult(DeadLetterRecord Record, bool Replayed, Guid RecoveryMessageId);

public enum DeadLetterSourceKind { SchedulerJob, OutboxMessage }
public enum DeadLetterFailureClass { TransientExhausted, Permanent, ContractViolation, SecurityViolation, Unknown }
public enum DeadLetterStatus { Quarantined, ReadyForRetry, Requeued, Discarded }

public sealed class DeadLetterConflictException : Exception
{
    public DeadLetterConflictException(Guid deadLetterId, string message) : base(message) => DeadLetterId = deadLetterId;
    public Guid DeadLetterId { get; }
}

public sealed class DeadLetterTransitionException : Exception
{
    public DeadLetterTransitionException(Guid deadLetterId, DeadLetterStatus status, string operation)
        : base($"Dead letter '{deadLetterId:D}' in state '{status}' cannot perform '{operation}'.")
    {
        DeadLetterId = deadLetterId;
        Status = status;
        Operation = operation;
    }

    public Guid DeadLetterId { get; }
    public DeadLetterStatus Status { get; }
    public string Operation { get; }
}

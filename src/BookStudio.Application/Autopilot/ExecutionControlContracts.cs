namespace BookStudio.Application.Autopilot;

public interface IExecutionControlStore
{
    ValueTask<ExecutionControlResult> ApplyAsync(
        ExecutionControlCommand command,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<ControlledExecution?> GetAsync(
        Guid executionId,
        CancellationToken cancellationToken = default);
}

public sealed record ExecutionControlCommand(
    Guid RequestId,
    Guid ExecutionId,
    ExecutionControlAction Action,
    string Actor,
    string Reason,
    string RequestFingerprint);

public sealed record ExecutionControlResult(
    ControlledExecution Execution,
    bool Replayed,
    Guid ControlMessageId);

public sealed record ControlledExecution(
    Guid ExecutionId,
    ExecutionControlStatus Status,
    long Version,
    string? LastActor,
    string? LastReason,
    DateTimeOffset UpdatedAtUtc,
    Guid? ActiveJobId);

public enum ExecutionControlAction
{
    Pause,
    Resume,
    Cancel,
}

public enum ExecutionControlStatus
{
    Runnable,
    Running,
    Paused,
    Cancelled,
}

public sealed class ExecutionControlConflictException : Exception
{
    public ExecutionControlConflictException(string message) : base(message) { }
}

public sealed class ExecutionControlTransitionException : Exception
{
    public ExecutionControlTransitionException(ExecutionControlStatus status, ExecutionControlAction action)
        : base($"Execution in state '{status}' cannot apply action '{action}'.")
    {
        Status = status;
        Action = action;
    }

    public ExecutionControlStatus Status { get; }
    public ExecutionControlAction Action { get; }
}

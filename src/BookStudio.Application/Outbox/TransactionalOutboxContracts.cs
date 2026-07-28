namespace BookStudio.Application.Outbox;

public interface ITransactionalOutboxUnitOfWork
{
    ValueTask<TransactionalOutboxResult> ExecuteAsync(
        TransactionalOutboxCommand command,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default);
}

public sealed record TransactionalOutboxCommand(
    string OperationId,
    string RequestFingerprint,
    string StateKey,
    string StateValue,
    IReadOnlyList<OutboxMessageDraft> Messages);

public sealed record TransactionalOutboxResult(
    string OperationId,
    bool Replayed,
    long StateVersion,
    IReadOnlyList<Guid> MessageIds);

public static class TransactionalOutboxErrorCodes
{
    public const string Invalid = "TRANSACTIONAL_OUTBOX_INVALID";
    public const string IdempotencyConflict = "TRANSACTIONAL_OUTBOX_IDEMPOTENCY_CONFLICT";
}

public sealed class TransactionalOutboxException : Exception
{
    public TransactionalOutboxException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

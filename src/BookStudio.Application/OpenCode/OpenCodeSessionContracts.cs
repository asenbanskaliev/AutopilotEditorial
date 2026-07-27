namespace BookStudio.Application.OpenCode;

public sealed record OpenCodeCreateSessionCommand(
    string? ParentSessionId,
    string? Title,
    string IdempotencyKey);

public sealed record OpenCodeTextPart(string Text);

public sealed record OpenCodeSendPromptCommand(
    string SessionId,
    IReadOnlyList<OpenCodeTextPart> Parts,
    string IdempotencyKey);

public sealed record OpenCodeSession(
    string Id,
    string? ParentId,
    string? Title,
    long? CreatedUnixMilliseconds,
    long? UpdatedUnixMilliseconds);

public static class OpenCodeSessionStatusTypes
{
    public const string Idle = "idle";
    public const string Busy = "busy";
    public const string Retry = "retry";
    public const string Unknown = "unknown";
}

public sealed record OpenCodeSessionStatus(
    string Type,
    int? Attempt,
    string? Message,
    long? NextUnixMilliseconds,
    string? ProviderType)
{
    public static OpenCodeSessionStatus Idle() =>
        new(OpenCodeSessionStatusTypes.Idle, null, null, null, null);

    public static OpenCodeSessionStatus Busy() =>
        new(OpenCodeSessionStatusTypes.Busy, null, null, null, null);

    public static OpenCodeSessionStatus Retry(
        int attempt,
        string message,
        long nextUnixMilliseconds) =>
        new(
            OpenCodeSessionStatusTypes.Retry,
            attempt,
            message,
            nextUnixMilliseconds,
            null);

    public static OpenCodeSessionStatus Unknown(string providerType) =>
        new(
            OpenCodeSessionStatusTypes.Unknown,
            null,
            null,
            null,
            providerType);
}

public sealed record OpenCodePromptSubmission(
    string SessionId,
    string IdempotencyKey,
    bool Accepted);

public sealed record OpenCodeAbortResult(
    string SessionId,
    bool Accepted);

public static class OpenCodeSessionErrorCodes
{
    public const string OpenCodeUnavailable = "opencode_unavailable";
    public const string OpenCodeAuthenticationRequired = "opencode_authentication_required";
    public const string OpenCodeUnhealthy = "opencode_unhealthy";
    public const string OpenCodeSessionFeaturesMissing = "opencode_session_features_missing";
    public const string SessionNotFound = "session_not_found";
    public const string SessionHttpStatus = "session_http_status";
    public const string SessionPayloadInvalid = "session_payload_invalid";
    public const string StatusHttpStatus = "status_http_status";
    public const string StatusPayloadInvalid = "status_payload_invalid";
    public const string PromptHttpStatus = "prompt_http_status";
    public const string AbortHttpStatus = "abort_http_status";
    public const string AbortPayloadInvalid = "abort_payload_invalid";
    public const string ResponseTooLarge = "response_too_large";
    public const string RequestTooLarge = "request_too_large";
    public const string RequestTimeout = "request_timeout";
    public const string ConnectionFailed = "connection_failed";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string IdempotencyCapacityExceeded = "idempotency_capacity_exceeded";
}

public sealed class OpenCodeSessionLifecycleException : Exception
{
    public OpenCodeSessionLifecycleException(string code)
        : base("OpenCode session lifecycle operation failed.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

namespace BookStudio.Application.OpenCode;

public static class OpenCodeEventScopes
{
    public const string Project = "project";
    public const string Global = "global";
    public const string Both = "both";
}

public static class OpenCodeEventSources
{
    public const string Project = "project";
    public const string Global = "global";
    public const string Poll = "poll";
}

public static class OpenCodeEventKinds
{
    public const string Connected = "connected";
    public const string SessionStatus = "session_status";
    public const string ProviderEvent = "provider_event";
    public const string Reconciliation = "reconciliation";
}

public static class OpenCodeReconciliationReasons
{
    public const string Initial = "initial";
    public const string Reconnect = "reconnect";
    public const string Eof = "eof";
    public const string Stall = "stall";
    public const string Malformed = "malformed";
    public const string Periodic = "periodic";
}

public sealed record OpenCodeEventWatchRequest(
    string Scope = OpenCodeEventScopes.Both,
    string? SessionIdFilter = null);

public sealed record OpenCodeReconciledEvent(
    long Sequence,
    string Source,
    string Kind,
    string ProviderType,
    string? ProviderEventId,
    string? SessionId,
    string? Directory,
    OpenCodeSessionStatus? Status,
    bool Synthetic,
    string? ReconciliationReason,
    long ObservedUnixMilliseconds);

public static class OpenCodeEventErrorCodes
{
    public const string OpenCodeUnavailable = "opencode_unavailable";
    public const string OpenCodeAuthenticationRequired = "opencode_authentication_required";
    public const string OpenCodeUnhealthy = "opencode_unhealthy";
    public const string OpenCodeEventFeaturesMissing = "opencode_event_features_missing";
    public const string SseHttpStatus = "sse_http_status";
    public const string SseContentTypeInvalid = "sse_content_type_invalid";
    public const string SseLineTooLarge = "sse_line_too_large";
    public const string SseEventTooLarge = "sse_event_too_large";
    public const string SseFieldLimitExceeded = "sse_field_limit_exceeded";
    public const string SseUtf8Invalid = "sse_utf8_invalid";
    public const string SsePayloadInvalid = "sse_payload_invalid";
    public const string SseProjectHandshakeInvalid = "sse_project_handshake_invalid";
    public const string SseStalled = "sse_stalled";
    public const string SseReconnectExhausted = "sse_reconnect_exhausted";
    public const string StatusHttpStatus = "status_http_status";
    public const string StatusPayloadInvalid = "status_payload_invalid";
    public const string ResponseTooLarge = "response_too_large";
    public const string RequestTimeout = "request_timeout";
    public const string ConnectionFailed = "connection_failed";
}

public static class OpenCodeEventValidation
{
    public const int MaximumProviderEventIdBytes = 256;
    public const int MaximumProviderTypeBytes = 128;
    public const int MaximumDirectoryBytes = 2048;

    public static void ValidateWatchRequest(OpenCodeEventWatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Scope is not (
            OpenCodeEventScopes.Project or
            OpenCodeEventScopes.Global or
            OpenCodeEventScopes.Both))
        {
            throw new ArgumentException("OpenCode event scope is invalid.", nameof(request));
        }
        if (request.SessionIdFilter is not null)
        {
            OpenCodeSessionValidation.ValidateSessionId(
                request.SessionIdFilter,
                nameof(request.SessionIdFilter));
        }
    }
}

public sealed class OpenCodeEventReconciliationException : Exception
{
    public OpenCodeEventReconciliationException(string code)
        : base("OpenCode event reconciliation failed.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

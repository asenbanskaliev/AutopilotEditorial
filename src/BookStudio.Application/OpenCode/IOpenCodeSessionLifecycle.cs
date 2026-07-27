namespace BookStudio.Application.OpenCode;

/// <summary>Provider-neutral bounded OpenCode session lifecycle use cases.</summary>
public interface IOpenCodeSessionLifecycle
{
    ValueTask<OpenCodeSession> CreateSessionAsync(
        OpenCodeCreateSessionCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<OpenCodeSession> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyDictionary<string, OpenCodeSessionStatus>> GetStatusesAsync(
        CancellationToken cancellationToken = default);

    ValueTask<OpenCodePromptSubmission> SendPromptAsync(
        OpenCodeSendPromptCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<OpenCodeAbortResult> AbortSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}

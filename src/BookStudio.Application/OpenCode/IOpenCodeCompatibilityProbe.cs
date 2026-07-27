namespace BookStudio.Application.OpenCode;

/// <summary>Determines whether the configured OpenCode host is healthy and exposes the required API surface.</summary>
public interface IOpenCodeCompatibilityProbe
{
    ValueTask<OpenCodeCompatibilityReport> ProbeAsync(
        CancellationToken cancellationToken = default);
}

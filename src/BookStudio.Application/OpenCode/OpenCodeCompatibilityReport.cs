namespace BookStudio.Application.OpenCode;

public static class OpenCodeCompatibilityStates
{
    public const string Compatible = "compatible";
    public const string Degraded = "degraded";
    public const string Unhealthy = "unhealthy";
    public const string AuthenticationRequired = "authentication_required";
    public const string Unavailable = "unavailable";
}

/// <summary>Provider-neutral, credential-free OpenCode compatibility result.</summary>
public sealed record OpenCodeCompatibilityReport(
    string State,
    string Code,
    string? ServerVersion,
    IReadOnlyList<string> DetectedFeatures,
    IReadOnlyList<string> MissingRequiredFeatures,
    IReadOnlyDictionary<string, string> Facts)
{
    public bool IsCompatible =>
        string.Equals(State, OpenCodeCompatibilityStates.Compatible, StringComparison.Ordinal);
}

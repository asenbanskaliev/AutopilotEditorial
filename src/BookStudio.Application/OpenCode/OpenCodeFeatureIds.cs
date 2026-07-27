namespace BookStudio.Application.OpenCode;

/// <summary>Stable OpenCode capabilities required by BookStudio workflows.</summary>
public static class OpenCodeFeatureIds
{
    public const string Health = "health";
    public const string ProvidersList = "providers.list";
    public const string AgentsList = "agents.list";
    public const string McpStatus = "mcp.status";
    public const string SessionsList = "sessions.list";
    public const string SessionsCreate = "sessions.create";
    public const string SessionsGet = "sessions.get";
    public const string SessionsStatus = "sessions.status";
    public const string SessionsPromptAsync = "sessions.prompt_async";
    public const string SessionsAbort = "sessions.abort";
    public const string EventsProject = "events.project";
    public const string EventsGlobal = "events.global";

    public static IReadOnlyList<string> Required { get; } =
    [
        AgentsList,
        EventsGlobal,
        EventsProject,
        Health,
        McpStatus,
        ProvidersList,
        SessionsAbort,
        SessionsCreate,
        SessionsGet,
        SessionsList,
        SessionsPromptAsync,
        SessionsStatus,
    ];
}

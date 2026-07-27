using BookStudio.Application.Diagnostics;

namespace BookStudio.Application.Operations;

public sealed record OperationsCapability(
    string Id,
    string Status,
    string Phase,
    string Description);

public sealed record OperationsStatusResult(
    string Status,
    int ProbeCount,
    int ReadyProbeCount,
    string AutopilotAvailability,
    IReadOnlyList<string> UnreadyProbes,
    IReadOnlyList<string> ReservedComponents);

public sealed record OperationsDiagnosticCheck(
    string Name,
    bool Ready,
    string Status,
    int? AppliedMigrationCount,
    int? LatestMigrationVersion);

public sealed record OperationsDiagnosticsResult(
    string Status,
    IReadOnlyList<OperationsDiagnosticCheck> Checks,
    IReadOnlyList<OperationsCapability> Capabilities,
    IReadOnlyList<string> Recommendations);

public static class OperationsCapabilityCatalog
{
    public static IReadOnlyList<OperationsCapability> All { get; } =
    [
        new("autopilot.pause-resume-cancel", "reserved", "F4", "Operational workflow controls are not implemented yet."),
        new("autopilot.replay", "reserved", "F4", "Durable replay is not implemented yet."),
        new("autopilot.scheduler", "reserved", "F4", "The durable scheduler is not implemented yet."),
        new("autopilot.worker", "reserved", "F4", "The durable worker is not implemented yet."),
        new("autopilot.workflow", "reserved", "F4", "AutopilotWorkflowRun and AutopilotJob are not implemented yet."),
        new("foundation.artifact-store", "available", "F1", "Immutable versioned artifact storage is available."),
        new("foundation.observability", "available", "F1", "OpenTelemetry baseline and sanitized snapshots are available."),
        new("foundation.outbox", "available", "F1", "Transactional Outbox foundation is available."),
        new("foundation.sqlite", "available", "F1", "SQLite WAL persistence and readiness checks are available."),
        new("mcp.book-authoring", "available", "F2", "Bounded deterministic authoring server is available."),
        new("mcp.book-core", "available", "F2", "Bounded core server is available."),
        new("mcp.book-ops", "available", "F2", "Bounded operations status and diagnostics server is available."),
        new("mcp.book-production", "available", "F2", "Bounded release and preflight server is available."),
        new("mcp.book-quality", "available", "F2", "Bounded deterministic quality server is available."),
        new("opencode.sessions", "reserved", "F3", "OpenCode session lifecycle is not implemented yet."),
    ];

    public static IReadOnlyList<string> ReservedIds { get; } = All
        .Where(capability => capability.Status == "reserved")
        .Select(capability => capability.Id)
        .Order(StringComparer.Ordinal)
        .ToArray();
}

public sealed class OperationsDiagnosticsException : Exception
{
    public OperationsDiagnosticsException(string code, string safeMessage)
        : base(safeMessage)
    {
        Code = code;
    }

    public string Code { get; }
}

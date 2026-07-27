using BookStudio.Application.Diagnostics;

namespace BookStudio.Application.Operations;

/// <summary>Aggregates sanitized readiness probes and the canonical product capability catalog.</summary>
public sealed class OperationsDiagnosticsService : IOperationsDiagnosticsService
{
    private readonly IReadOnlyList<IReadinessProbe> _probes;
    private readonly IReadOnlyList<OperationsCapability> _capabilities;

    public OperationsDiagnosticsService(
        IEnumerable<IReadinessProbe> probes,
        IReadOnlyList<OperationsCapability>? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(probes);
        _probes = probes
            .OrderBy(probe => probe.Name, StringComparer.Ordinal)
            .ToArray();
        if (_probes.Count == 0 ||
            _probes.Any(probe => string.IsNullOrWhiteSpace(probe.Name)) ||
            _probes.Select(probe => probe.Name).Distinct(StringComparer.Ordinal).Count() != _probes.Count)
        {
            throw new ArgumentException("Operations diagnostics require uniquely named readiness probes.", nameof(probes));
        }

        _capabilities = (capabilities ?? OperationsCapabilityCatalog.All)
            .OrderBy(capability => capability.Id, StringComparer.Ordinal)
            .ToArray();
        if (_capabilities.Count == 0 ||
            _capabilities.Any(capability =>
                string.IsNullOrWhiteSpace(capability.Id) ||
                capability.Status is not "available" and not "reserved" ||
                string.IsNullOrWhiteSpace(capability.Phase) ||
                string.IsNullOrWhiteSpace(capability.Description)) ||
            _capabilities.Select(capability => capability.Id)
                .Distinct(StringComparer.Ordinal).Count() != _capabilities.Count)
        {
            throw new ArgumentException("Operations capability catalog is invalid.", nameof(capabilities));
        }
    }

    public async ValueTask<OperationsStatusResult> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var checks = await CheckAllAsync(cancellationToken).ConfigureAwait(false);
        var overall = ResolveOverallStatus(checks);
        return new OperationsStatusResult(
            overall,
            checks.Count,
            checks.Count(check => check.Ready),
            "unavailable",
            checks.Where(check => !check.Ready)
                .Select(check => check.Name)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            _capabilities.Where(capability => capability.Status == "reserved")
                .Select(capability => capability.Id)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    public async ValueTask<OperationsDiagnosticsResult> RunDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        var checks = await CheckAllAsync(cancellationToken).ConfigureAwait(false);
        var overall = ResolveOverallStatus(checks);
        var recommendations = new SortedSet<string>(StringComparer.Ordinal);

        if (checks.Any(check => !check.Ready && check.Status == "missing"))
        {
            recommendations.Add("initialize_workspace_via_control_center");
        }
        if (checks.Any(check => !check.Ready && check.Status != "missing"))
        {
            recommendations.Add("inspect_control_center_readiness");
        }
        if (_capabilities.Any(capability => capability.Id == "opencode.sessions" && capability.Status == "reserved"))
        {
            recommendations.Add("complete_f3_opencode_before_model_sessions");
        }
        if (_capabilities.Any(capability => capability.Id == "autopilot.workflow" && capability.Status == "reserved"))
        {
            recommendations.Add("complete_f4_autopilot_before_workflow_controls");
        }

        return new OperationsDiagnosticsResult(
            overall,
            checks,
            _capabilities,
            recommendations.ToArray());
    }

    public static string BuildOperationId(
        string operation,
        string status,
        IEnumerable<OperationsDiagnosticCheck> checks)
    {
        var canonical = string.Join(
            '|',
            new[] { operation, status }
                .Concat(checks.Select(check =>
                    $"{check.Name}:{check.Ready}:{check.Status}:{check.AppliedMigrationCount}:{check.LatestMigrationVersion}")));
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    private async ValueTask<IReadOnlyList<OperationsDiagnosticCheck>> CheckAllAsync(
        CancellationToken cancellationToken)
    {
        var checks = new List<OperationsDiagnosticCheck>(_probes.Count);
        foreach (var probe in _probes)
        {
            try
            {
                var result = await probe.CheckAsync(cancellationToken).ConfigureAwait(false);
                checks.Add(new OperationsDiagnosticCheck(
                    BoundName(result.Name, probe.Name),
                    result.Ready,
                    BoundStatus(result.Status),
                    result.AppliedMigrationCount,
                    result.LatestMigrationVersion));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                checks.Add(new OperationsDiagnosticCheck(
                    BoundName(probe.Name, "unknown-probe"),
                    false,
                    "error",
                    null,
                    null));
            }
        }
        return checks;
    }

    private static string ResolveOverallStatus(IReadOnlyList<OperationsDiagnosticCheck> checks)
    {
        var ready = checks.Count(check => check.Ready);
        if (ready == checks.Count)
        {
            return "ready";
        }
        return ready == 0 ? "notReady" : "degraded";
    }

    private static string BoundName(string? candidate, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(candidate) ? fallback : candidate;
        var bounded = new string(value
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .Take(64)
            .ToArray());
        return string.IsNullOrWhiteSpace(bounded) ? "unknown-probe" : bounded;
    }

    private static string BoundStatus(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return "unknown";
        }
        var bounded = new string(candidate
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .Take(32)
            .ToArray());
        return string.IsNullOrWhiteSpace(bounded) ? "unknown" : bounded;
    }
}

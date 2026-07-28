using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookStudio.Application.Autopilot;

public sealed record WorkflowStepDefinition(
    string StepId,
    string JobType,
    string SchemaVersion,
    string ToolProfileId,
    string ModelRole,
    int TimeoutSeconds,
    int MaximumAttempts,
    IReadOnlyList<string> DependsOn);

public sealed record WorkflowDefinition(
    string WorkflowId,
    string Version,
    IReadOnlyList<WorkflowStepDefinition> Steps);

public sealed class WorkflowCatalog
{
    private const int MaximumWorkflows = 1_000;
    private const int MaximumStepsPerWorkflow = 10_000;
    private readonly IReadOnlyDictionary<string, WorkflowDefinition> _definitions;

    public WorkflowCatalog(
        IEnumerable<WorkflowDefinition> definitions,
        IReadOnlySet<string> approvedToolProfiles,
        IReadOnlySet<string> approvedModelRoles)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(approvedToolProfiles);
        ArgumentNullException.ThrowIfNull(approvedModelRoles);

        var normalized = definitions.Select(definition => Normalize(definition, approvedToolProfiles, approvedModelRoles)).ToArray();
        if (normalized.Length is < 1 or > MaximumWorkflows)
        {
            throw Invalid("Workflow catalog size is invalid.");
        }

        var map = new Dictionary<string, WorkflowDefinition>(StringComparer.Ordinal);
        foreach (var definition in normalized)
        {
            if (!map.TryAdd(Key(definition.WorkflowId, definition.Version), definition))
            {
                throw Invalid($"Duplicate workflow '{definition.WorkflowId}' version '{definition.Version}'.");
            }
        }

        _definitions = new System.Collections.ObjectModel.ReadOnlyDictionary<string, WorkflowDefinition>(map);
        Fingerprint = ComputeFingerprint(map.Values);
    }

    public string Fingerprint { get; }

    public IReadOnlyCollection<WorkflowDefinition> Definitions =>
        Array.AsReadOnly(_definitions.Values.OrderBy(item => item.WorkflowId, StringComparer.Ordinal)
            .ThenBy(item => item.Version, StringComparer.Ordinal).ToArray());

    public WorkflowDefinition Resolve(string workflowId, string version)
    {
        ValidateToken(workflowId, nameof(workflowId), 256);
        ValidateToken(version, nameof(version), 64);
        return _definitions.TryGetValue(Key(workflowId, version), out var definition)
            ? definition
            : throw new KeyNotFoundException($"Workflow '{workflowId}' version '{version}' was not found.");
    }

    private static WorkflowDefinition Normalize(
        WorkflowDefinition source,
        IReadOnlySet<string> approvedToolProfiles,
        IReadOnlySet<string> approvedModelRoles)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateToken(source.WorkflowId, nameof(source.WorkflowId), 256);
        ValidateToken(source.Version, nameof(source.Version), 64);
        if (source.Steps is null || source.Steps.Count is < 1 or > MaximumStepsPerWorkflow)
        {
            throw Invalid("Workflow steps are invalid.");
        }

        var stepMap = new Dictionary<string, WorkflowStepDefinition>(StringComparer.Ordinal);
        foreach (var step in source.Steps)
        {
            ArgumentNullException.ThrowIfNull(step);
            ValidateToken(step.StepId, nameof(step.StepId), 256);
            ValidateToken(step.JobType, nameof(step.JobType), 256);
            ValidateToken(step.SchemaVersion, nameof(step.SchemaVersion), 64);
            ValidateToken(step.ToolProfileId, nameof(step.ToolProfileId), 256);
            ValidateToken(step.ModelRole, nameof(step.ModelRole), 256);
            if (!approvedToolProfiles.Contains(step.ToolProfileId))
            {
                throw Invalid($"Unknown tool profile '{step.ToolProfileId}'.");
            }
            if (!approvedModelRoles.Contains(step.ModelRole))
            {
                throw Invalid($"Unknown model role '{step.ModelRole}'.");
            }
            if (step.TimeoutSeconds is < 1 or > 86_400 || step.MaximumAttempts is < 1 or > 100)
            {
                throw Invalid($"Execution policy for step '{step.StepId}' is invalid.");
            }

            var dependencies = NormalizeDependencies(step.StepId, step.DependsOn);
            var normalized = step with { DependsOn = dependencies };
            if (!stepMap.TryAdd(step.StepId, normalized))
            {
                throw Invalid($"Duplicate step '{step.StepId}'.");
            }
        }

        foreach (var step in stepMap.Values)
        {
            foreach (var dependency in step.DependsOn)
            {
                if (!stepMap.ContainsKey(dependency))
                {
                    throw Invalid($"Step '{step.StepId}' references unknown dependency '{dependency}'.");
                }
            }
        }
        EnsureAcyclic(stepMap);

        var ordered = stepMap.Values.OrderBy(item => item.StepId, StringComparer.Ordinal).ToArray();
        return new WorkflowDefinition(source.WorkflowId, source.Version, Array.AsReadOnly(ordered));
    }

    private static IReadOnlyList<string> NormalizeDependencies(string stepId, IReadOnlyList<string> source)
    {
        if (source is null || source.Count > MaximumStepsPerWorkflow)
        {
            throw Invalid($"Dependencies for step '{stepId}' are invalid.");
        }
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in source)
        {
            ValidateToken(dependency, nameof(source), 256);
            if (dependency == stepId || !unique.Add(dependency))
            {
                throw Invalid($"Dependencies for step '{stepId}' are invalid.");
            }
        }
        return Array.AsReadOnly(unique.OrderBy(item => item, StringComparer.Ordinal).ToArray());
    }

    private static void EnsureAcyclic(IReadOnlyDictionary<string, WorkflowStepDefinition> steps)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var stepId in steps.Keys)
        {
            Visit(stepId, steps, state);
        }
    }

    private static void Visit(
        string stepId,
        IReadOnlyDictionary<string, WorkflowStepDefinition> steps,
        IDictionary<string, int> state)
    {
        if (state.TryGetValue(stepId, out var current))
        {
            if (current == 1)
            {
                throw Invalid("Workflow dependency cycle detected.");
            }
            if (current == 2)
            {
                return;
            }
        }
        state[stepId] = 1;
        foreach (var dependency in steps[stepId].DependsOn)
        {
            Visit(dependency, steps, state);
        }
        state[stepId] = 2;
    }

    private static string ComputeFingerprint(IEnumerable<WorkflowDefinition> definitions)
    {
        var builder = new StringBuilder();
        foreach (var workflow in definitions.OrderBy(item => item.WorkflowId, StringComparer.Ordinal)
                     .ThenBy(item => item.Version, StringComparer.Ordinal))
        {
            builder.Append(workflow.WorkflowId).Append('\u001f').Append(workflow.Version).Append('\n');
            foreach (var step in workflow.Steps.OrderBy(item => item.StepId, StringComparer.Ordinal))
            {
                builder.Append(step.StepId).Append('\u001f')
                    .Append(step.JobType).Append('\u001f')
                    .Append(step.SchemaVersion).Append('\u001f')
                    .Append(step.ToolProfileId).Append('\u001f')
                    .Append(step.ModelRole).Append('\u001f')
                    .Append(step.TimeoutSeconds).Append('\u001f')
                    .Append(step.MaximumAttempts).Append('\u001f')
                    .AppendJoin(',', step.DependsOn.OrderBy(item => item, StringComparer.Ordinal))
                    .Append('\n');
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string Key(string workflowId, string version) => $"{workflowId}\u001f{version}";

    private static void ValidateToken(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw Invalid($"{parameterName} is invalid.");
        }
    }

    private static ArgumentException Invalid(string message) => new(message);
}

public static class WorkflowCatalogJsonLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    public static WorkflowCatalog Load(
        string json,
        IReadOnlySet<string> approvedToolProfiles,
        IReadOnlySet<string> approvedModelRoles)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4_194_304)
        {
            throw new ArgumentException("Workflow catalog JSON is invalid.", nameof(json));
        }
        var document = JsonSerializer.Deserialize<WorkflowCatalogDocument>(json, Options)
            ?? throw new ArgumentException("Workflow catalog JSON is invalid.", nameof(json));
        if (document.SchemaVersion != "1.0.0")
        {
            throw new ArgumentException("Unsupported workflow catalog schema version.", nameof(json));
        }
        return new WorkflowCatalog(document.Workflows, approvedToolProfiles, approvedModelRoles);
    }

    private sealed record WorkflowCatalogDocument(string SchemaVersion, IReadOnlyList<WorkflowDefinition> Workflows);
}

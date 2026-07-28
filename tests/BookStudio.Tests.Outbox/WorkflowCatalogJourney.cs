using System.Text.Json;
using BookStudio.Application.Autopilot;

namespace BookStudio.Tests.Outbox;

internal static class WorkflowCatalogJourney
{
    public static void Run(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot, "config", "autopilot", "workflows.json");
        var json = File.ReadAllText(path);
        var tools = new HashSet<string>(StringComparer.Ordinal)
        {
            "editorial-discovery",
            "editorial-authoring",
            "editorial-quality",
        };
        var roles = new HashSet<string>(StringComparer.Ordinal)
        {
            "planner",
            "architect",
            "writer",
            "reviewer",
        };

        var catalog = WorkflowCatalogJsonLoader.Load(json, tools, roles);
        var authoring = catalog.Resolve("book-authoring", "1.0.0");
        Require(authoring.Steps.Count == 3, "Repository workflow was not loaded.");
        Require(authoring.Steps.Single(step => step.StepId == "draft").DependsOn.SequenceEqual(["specify"]),
            "Workflow dependencies were not preserved.");
        Require(catalog.Fingerprint.Length == 64 && catalog.Fingerprint.All(Uri.IsHexDigit),
            "Catalog fingerprint is invalid.");
        RequireThrows<KeyNotFoundException>(() => catalog.Resolve("book-authoring", "2.0.0"));

        var reordered = new WorkflowCatalog(
            catalog.Definitions.Reverse().Select(workflow => workflow with { Steps = workflow.Steps.Reverse().ToArray() }),
            tools,
            roles);
        Require(reordered.Fingerprint == catalog.Fingerprint, "Fingerprint depends on input ordering.");

        RequireThrows<ArgumentException>(() => new WorkflowCatalog(
            [Definition("cycle", [Step("a", ["b"]), Step("b", ["a"])])], tools, roles));
        RequireThrows<ArgumentException>(() => new WorkflowCatalog(
            [Definition("missing", [Step("a", ["missing-step"])])], tools, roles));
        RequireThrows<ArgumentException>(() => new WorkflowCatalog(
            [Definition("tool", [Step("a", [], toolProfileId: "unknown-tool")])], tools, roles));
        RequireThrows<ArgumentException>(() => new WorkflowCatalog(
            [Definition("role", [Step("a", [], modelRole: "unknown-role")])], tools, roles));
        RequireThrows<ArgumentException>(() => new WorkflowCatalog(
            [Definition("duplicate", [Step("a", []), Step("a", [])])], tools, roles));
        RequireThrows<ArgumentException>(() => WorkflowCatalogJsonLoader.Load(
            json.Replace("\"SchemaVersion\": \"1.0.0\"", "\"SchemaVersion\": \"2.0.0\"", StringComparison.Ordinal),
            tools,
            roles));
        RequireThrows<JsonException>(() => WorkflowCatalogJsonLoader.Load(
            json.Replace("\"Workflows\":", "\"Unknown\": true, \"Workflows\":", StringComparison.Ordinal),
            tools,
            roles));

        var definitions = catalog.Definitions;
        RequireThrows<NotSupportedException>(() => ((ICollection<WorkflowDefinition>)definitions).Add(authoring));
        RequireThrows<NotSupportedException>(() => ((IList<WorkflowStepDefinition>)authoring.Steps).Add(Step("x", [])));
    }

    private static WorkflowDefinition Definition(string id, IReadOnlyList<WorkflowStepDefinition> steps) =>
        new(id, "1.0.0", steps);

    private static WorkflowStepDefinition Step(
        string id,
        IReadOnlyList<string> dependencies,
        string toolProfileId = "editorial-authoring",
        string modelRole = "writer") =>
        new(id, $"job.{id}", "1.0.0", toolProfileId, modelRole, 60, 3, dependencies);

    private static void RequireThrows<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

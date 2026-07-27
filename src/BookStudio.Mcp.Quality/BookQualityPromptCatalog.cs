using BookStudio.Mcp.Prompts;

namespace BookStudio.Mcp.Quality;

/// <summary>Versioned user-controlled prompts for the quality bounded context.</summary>
public static class BookQualityPromptCatalog
{
    public static VersionedMcpPromptCatalog Catalog { get; } = new(
    [
        new VersionedMcpPrompt(
            version: "1",
            resourceUri: "book://prompts/book-quality/assess-draft/v1",
            title: "Assess immutable draft quality",
            description: "Guide deterministic audit and draft-basic gate evaluation for one immutable draft version.",
            arguments:
            [
                new("artifactId", "Draft artifact ID", "Project-scoped draft artifact identifier.", true),
                new("projectId", "Project ID", "Lowercase project slug used to enforce draft scope.", true),
                new("version", "Version", "Canonical positive draft version.", true),
            ],
            messageTemplate:
                "Assess draft {{artifactId}} version {{version}} in project {{projectId}}. Use book.audit.run and book.gate.evaluate with draft-basic. Distinguish pass, warn, fail and PASS/BLOCKED. Do not call reserved repair or memory tools.",
            renderer: arguments =>
            {
                PromptArgumentRules.RequireExactArguments(
                    arguments,
                    "projectId",
                    "artifactId",
                    "version");
                var projectId = PromptArgumentRules.ProjectId(arguments);
                var artifactId = PromptArgumentRules.ArtifactId(
                    arguments,
                    projectId,
                    requiredSegment: "draft");
                var version = PromptArgumentRules.Version(arguments);
                return $"Assess immutable draft {artifactId} version {version} in project {projectId}. First call book.audit.run and explain each deterministic check as pass, warn or fail. Then call book.gate.evaluate with profile draft-basic and report PASS or BLOCKED plus the exact blockingReasons. Do not call the reserved repair or memory tools, and do not present these deterministic checks as complete narrative editing.";
            }),
    ],
    cursorScope: "quality-prompts");
}

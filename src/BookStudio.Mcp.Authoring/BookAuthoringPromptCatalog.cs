using BookStudio.Mcp.Prompts;

namespace BookStudio.Mcp.Authoring;

/// <summary>Versioned user-controlled prompts for the authoring bounded context.</summary>
public static class BookAuthoringPromptCatalog
{
    public static VersionedMcpPromptCatalog Catalog { get; } = new(
    [
        new VersionedMcpPrompt(
            name: "book.authoring.validate-draft.v1",
            version: "1",
            resourceUri: "book://prompts/book-authoring/validate-draft/v1",
            title: "Validate immutable draft",
            description: "Guide deterministic structural validation of one stored draft version without creating a new version.",
            arguments:
            [
                new("artifactId", "Draft artifact ID", "Project-scoped draft artifact identifier.", true),
                new("projectId", "Project ID", "Lowercase project slug used to enforce draft scope.", true),
                new("version", "Version", "Canonical positive draft version.", true),
            ],
            messageTemplate:
                "Validate draft {{artifactId}} version {{version}} in project {{projectId}}. Use book.draft.validate, explain metrics and warnings, do not register another version without separate authorization, and do not present structural validation as complete linguistic editing.",
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
                return $"Validate immutable draft {artifactId} version {version} in project {projectId}. Call book.draft.validate and explain the returned metrics, validity and warning categories precisely. Do not register or overwrite another draft version without a separate explicit user instruction. State that this deterministic structural validation is not a complete linguistic, narrative or editorial review.";
            }),
    ],
    cursorScope: "authoring-prompts");
}

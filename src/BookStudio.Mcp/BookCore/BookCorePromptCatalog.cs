using BookStudio.Mcp.Prompts;

namespace BookStudio.Mcp.BookCore;

/// <summary>Versioned user-controlled prompts for the book-core bounded context.</summary>
public static class BookCorePromptCatalog
{
    public static VersionedMcpPromptCatalog Catalog { get; } = new(
    [
        new VersionedMcpPrompt(
            name: "book.core.inspect-artifact.v1",
            version: "1",
            resourceUri: "book://prompts/book-core/inspect-artifact/v1",
            title: "Inspect immutable artifact",
            description: "Guide a bounded inspection of one immutable artifact version using the active book-core tools.",
            arguments:
            [
                new("artifactId", "Artifact ID", "Project-scoped immutable artifact identifier.", true),
                new("projectId", "Project ID", "Lowercase project slug used to enforce artifact scope.", true),
                new("version", "Version", "Canonical positive artifact version.", true),
            ],
            messageTemplate:
                "Inspect immutable artifact {{artifactId}} version {{version}} in project {{projectId}}. Use book.artifact.get with bounded text when available. Report metadata and integrity without inventing missing content. Use book.artifact.compare only when the user separately provides another version.",
            renderer: arguments =>
            {
                PromptArgumentRules.RequireExactArguments(
                    arguments,
                    "projectId",
                    "artifactId",
                    "version");
                var projectId = PromptArgumentRules.ProjectId(arguments);
                var artifactId = PromptArgumentRules.ArtifactId(arguments, projectId);
                var version = PromptArgumentRules.Version(arguments);
                return $"Inspect immutable artifact {artifactId} version {version} in project {projectId}. Use book.artifact.get with includeText enabled only for bounded text-compatible content. Report the returned metadata, media type, hash, length and integrity evidence without inventing content that is absent. Use book.artifact.compare only when the user separately supplies another explicit version.";
            }),
    ],
    cursorScope: "core-prompts");
}

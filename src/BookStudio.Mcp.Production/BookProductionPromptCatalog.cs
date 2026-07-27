using BookStudio.Mcp.Prompts;

namespace BookStudio.Mcp.Production;

/// <summary>Versioned user-controlled prompts for the production bounded context.</summary>
public static class BookProductionPromptCatalog
{
    public static VersionedMcpPromptCatalog Catalog { get; } = new(
    [
        new VersionedMcpPrompt(
            name: "book.production.preflight-release.v1",
            version: "1",
            resourceUri: "book://prompts/book-production/preflight-release/v1",
            title: "Preflight immutable release",
            description: "Guide deterministic release-basic preflight for one immutable release manifest.",
            arguments:
            [
                new("projectId", "Project ID", "Lowercase project slug used to enforce release scope.", true),
                new("releaseArtifactId", "Release artifact ID", "Project-scoped immutable release manifest identifier.", true),
                new("version", "Version", "Canonical positive release version.", true),
            ],
            messageTemplate:
                "Preflight release {{releaseArtifactId}} version {{version}} in project {{projectId}}. Use book.preflight.run with release-basic, report checks and blockingReasons, do not claim complete KDP compliance, and do not call reserved render, package or publish tools.",
            renderer: arguments =>
            {
                PromptArgumentRules.RequireExactArguments(
                    arguments,
                    "projectId",
                    "releaseArtifactId",
                    "version");
                var projectId = PromptArgumentRules.ProjectId(arguments);
                var releaseArtifactId = PromptArgumentRules.ArtifactId(
                    arguments,
                    projectId,
                    name: "releaseArtifactId",
                    requiredSegment: "release");
                var version = PromptArgumentRules.Version(arguments);
                return $"Run deterministic preflight for immutable release {releaseArtifactId} version {version} in project {projectId}. Call book.preflight.run with profile release-basic, report every check and the exact blockingReasons, and distinguish PASS from BLOCKED. Do not claim complete Amazon KDP compliance and do not call reserved asset, render, package or publish tools.";
            }),
    ],
    cursorScope: "production-prompts");
}

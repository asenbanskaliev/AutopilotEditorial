using BookStudio.Mcp.Prompts;

namespace BookStudio.Mcp.Ops;

/// <summary>Versioned user-controlled prompts for the operations bounded context.</summary>
public static class BookOpsPromptCatalog
{
    public static VersionedMcpPromptCatalog Catalog { get; } = new(
    [
        new VersionedMcpPrompt(
            version: "1",
            resourceUri: "book://prompts/book-ops/inspect-readiness/v1",
            title: "Inspect BookStudio readiness",
            description: "Guide read-only operational status and diagnostics without invoking reserved Autopilot controls.",
            arguments: [],
            messageTemplate:
                "Inspect BookStudio readiness. Use book.ops.status first, then book.ops.diagnostics when status is not ready or detailed capability information is needed. Explain available and reserved capabilities. Do not call reserved Autopilot controls.",
            renderer: arguments =>
            {
                PromptArgumentRules.RequireNoArguments(arguments);
                return "Inspect BookStudio readiness without modifying the workspace. Call book.ops.status first. Call book.ops.diagnostics when the status is not ready or when detailed readiness checks, recommendations or capability availability are needed. Explain the difference between available and reserved capabilities, and do not call any reserved Autopilot start, status, pause, resume, cancel or replay tool.";
            }),
    ],
    cursorScope: "ops-prompts");
}

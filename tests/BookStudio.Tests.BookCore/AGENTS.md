# book-core Integration Test Instructions

## Allowed

- Seed immutable artifacts through the real `FileArtifactStore`.
- Launch the real `BookStudio.Mcp.dll` process with a disposable `--workspace-root`.
- Exercise initialize, tools and resources through redirected stdio.
- Use synthetic text, binary and oversize artifacts.
- Validate structuredContent, schemas, annotations, cursors and project confinement.

## Forbidden

- Mock handlers or direct router invocation as the only evidence.
- Advertise reserved project/decision tools.
- Accept physical filesystem paths in MCP responses.
- Use fixed delays, fixed ports or external network calls.
- Weaken the VS-020 lifecycle journey; capability changes require the approved TCR.

The child process must be closed by EOF or killed during cleanup, and every disposable workspace must be removed best-effort.

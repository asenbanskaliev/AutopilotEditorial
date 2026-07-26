# BookStudio.Tests.BookAuthoring Agent Rules

## Allowed

- Launch the real `BookStudio.Mcp.Authoring.dll` child process.
- Use stdio JSON-RPC as the end-to-end product boundary.
- Use a disposable workspace and real immutable Artifact Store writes.
- Chain requests and responses deterministically without fixed sleeps.
- Verify server identity, capabilities, schemas, tool results, resources and EOF.
- Verify stdout contains protocol JSON only and stderr contains bounded diagnostic codes only.

## Forbidden

- Do not invoke the router or Application service directly as the sole acceptance proof.
- Do not use mocks instead of the child process for the external GREEN gate.
- Do not weaken reserved-tool, path-leak, version-conflict or lazy-workspace assertions.
- Do not preserve test workspaces after completion.
- Do not print draft content, workspace paths or secrets in diagnostics.

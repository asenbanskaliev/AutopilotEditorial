# BookStudio.Tests.BookOps Agent Rules

## Allowed

- Launch the real `BookStudio.Mcp.Ops.dll` child process.
- Observe a missing workspace through stdio JSON-RPC and prove it remains missing.
- Initialize the SQLite fixture through the real Infrastructure lifecycle before the ready-state journey.
- Verify identity, capabilities, tools, resources, status, diagnostics and capability parity.
- Warm SQLite reads before comparing file inventories to prove repeated ops calls are read-only.
- Use disposable workspaces and deterministic request/response chaining.

## Forbidden

- Do not mock readiness as the sole external GREEN proof.
- Do not invoke the ops router or Application service directly as the product boundary.
- Do not initialize or repair SQLite from the ops process.
- Do not simulate Autopilot workflow state or expose reserved controls.
- Do not weaken missing-workspace, no-mutation, no-path, stdout, stderr or reserved-tool assertions.
- Do not print workspace paths, database names, environment variables or secrets.

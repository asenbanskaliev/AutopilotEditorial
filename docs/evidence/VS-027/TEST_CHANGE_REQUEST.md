# VS-027 — TestChangeRequest TCR-027-001

## Trigger

The initial governance contract required the success marker `MCP_CONFORMANCE_PASS` to appear inside `McpConformanceRunner.cs`. The implemented separation assigns:

- `McpConformanceRunner` — five-server execution, corpus, deterministic generation, SHA-256 report and assertions;
- `Program.cs` — process exit code and the single user-visible success/failure line.

Putting the console marker in the runner would mix protocol execution with presentation.

## Approved governance change

- Continue requiring all five assemblies, seed `27027`, `128`, `SHA256` and transport limit usage in `McpConformanceRunner.cs`.
- Require `MCP_CONFORMANCE_PASS` and all report fields in `Program.cs`.
- Preserve subprocess execution as the external proof.

## Preserved requirements

- five real MCP processes;
- malformed corpus;
- 128 deterministic generated cases per server;
- SHA-256 reproducibility;
- exact success marker;
- no-crash, no-hang, no-leak, lazy workspace and EOF.

## Test Auditor decision

**APPROVED** — source ownership is corrected without reducing any observable requirement.

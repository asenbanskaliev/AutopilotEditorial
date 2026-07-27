# BookStudio.Tests.McpConformance Agent Rules

## Allowed

- Launch every real bounded MCP executable through `dotnet` and stdio.
- Use a versioned embedded corpus of deterministic malformed requests.
- Generate reproducible bounded invalid cases from the fixed seed.
- Assert JSON-RPC codes, lifecycle, recovery, capabilities, lazy workspace, safe stderr and EOF.
- Fail immediately on timeout, extra response, non-JSON stdout, crash or leaked canary data.

## Forbidden

- Do not call `McpSession`, routers, Application services or runtimes directly.
- Do not mock process behavior or replace an executable with an in-memory adapter.
- Do not use nondeterministic random seeds, network access, models or external fuzzing services.
- Do not execute mutating product tools.
- Do not retry a failed case in a way that hides a crash or hang.
- Do not weaken the five-server matrix, oversize recovery, no-leak, no-workspace or EOF assertions.

# BookStudio.Tests.OpenCodeSessionLifecycle Agent Rules

## Allowed

- Run a loopback-only contractual HTTP server owned by the test process.
- Exercise the real compatibility probe and real session lifecycle adapter over sockets.
- Verify create, get, status, async prompt, abort, idempotency, auth, bounds, timeout and cancellation.
- Record bounded HTTP method, path, headers and request body required for assertions.
- Use deterministic session IDs, JSON and OS-assigned loopback ports.

## Forbidden

- Do not contact public OpenCode servers or external networks.
- Do not invoke models, providers, tools, shell, commands, file operations or SSE.
- Do not mock `IOpenCodeSessionLifecycle` or bypass the HTTP adapter.
- Do not print prompt text, endpoint credentials, Authorization values or response bodies.
- Do not retry provider failures except where the idempotency-release contract explicitly requires a later independent call.

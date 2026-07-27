# BookStudio.Tests.OpenCodeCompatibility Agent Rules

## Allowed

- Run a loopback-only contractual HTTP server owned by the test process.
- Exercise the real `BookStudio.OpenCode` HTTP adapter through sockets.
- Verify health, version, OpenAPI feature detection, Basic auth, bounds, timeout and cancellation.
- Record every HTTP method/path/header required to prove read-only discovery.
- Use deterministic JSON and isolated ports assigned by the operating system.

## Forbidden

- Do not contact public OpenCode servers or external networks.
- Do not create sessions, send prompts, invoke models or mutate providers/MCP configuration.
- Do not mock `IOpenCodeCompatibilityProbe` or call the inspector instead of the adapter journey.
- Do not print endpoint credentials, Authorization headers or response bodies.
- Do not retry failed compatibility probes.

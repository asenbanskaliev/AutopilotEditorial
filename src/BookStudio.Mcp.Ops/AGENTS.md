# BookStudio.Mcp.Ops Agent Rules

## Allowed

- Act as a bounded MCP protocol adapter and composition root.
- Reuse lifecycle, JSON-RPC models, tool definitions, cursors and stdio transport from `BookStudio.Mcp`.
- Invoke only provider-neutral read-only operations use cases.
- Compose SQLite readiness lazily and call health checks without initializing or repairing storage.
- Publish bounded schemas, the canonical capability resource and structured diagnostics.
- Return stable sanitized statuses, recommendations and capability identifiers.

## Forbidden

- Do not create, start, pause, resume, cancel or replay Autopilot workflows.
- Do not advertise or dispatch reserved Autopilot tools.
- Do not call OpenCode, models, networks, shell processes or repair functions.
- Never initialize SQLite from status or diagnostics.
- Never write banners, paths, payloads or diagnostics to stdout.
- Do not expose workspace/database paths, connection strings, environment variables, secrets or exception details.
- Do not move operations business rules from Application into the protocol adapter.

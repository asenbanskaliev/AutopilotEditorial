# BookStudio.Mcp.Authoring Agent Rules

- This project is a bounded MCP protocol adapter and composition root.
- Reuse the lifecycle and stdio transport from `BookStudio.Mcp`.
- Do not implement editorial generation, model calls or workflow orchestration here.
- Active tools require real Application use cases.
- Never write banners, logs, payloads or diagnostics to stdout.
- Do not expose physical paths, secrets or exception details.
- Keep authoring writes immutable, bounded and project-scoped.
- Reserved tools must not be advertised or dispatched.

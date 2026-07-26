# BookStudio.Mcp.Authoring Agent Rules

## Allowed

- Act as a bounded MCP protocol adapter and composition root.
- Reuse lifecycle, JSON-RPC models and stdio transport from `BookStudio.Mcp`.
- Invoke only real provider-neutral Application use cases.
- Publish schemas, deterministic listings and bounded structured results.
- Compose the Artifact Store lazily from a canonical workspace root.
- Keep authoring writes immutable, bounded and project-scoped.

## Forbidden

- Do not implement editorial generation, model calls or workflow orchestration here.
- Do not advertise or dispatch reserved tools.
- Never write banners, logs, payloads or diagnostics to stdout.
- Do not expose physical paths, secrets, prompts, content in diagnostics or exception details.
- Do not overwrite or mutate an existing artifact version.
- Do not bypass Application by placing authoring business rules in the protocol adapter.

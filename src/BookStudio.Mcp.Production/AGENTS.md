# BookStudio.Mcp.Production Agent Rules

## Allowed

- Act as a bounded MCP protocol adapter and composition root.
- Reuse lifecycle, JSON-RPC models, tool definitions, cursors and stdio transport from `BookStudio.Mcp`.
- Invoke only provider-neutral Application production use cases.
- Publish bounded schemas, preflight profile resources and structured results.
- Compose the Artifact Store lazily from a canonical workspace root.
- Preserve immutable release versions and deterministic source ordering.

## Forbidden

- Do not render, publish, call networks, execute external processes or invoke models.
- Do not advertise or dispatch reserved asset/render/package tools.
- Never overwrite release versions or mutate source artifacts.
- Never write banners, source bytes, payloads or diagnostics to stdout.
- Do not expose physical paths, secrets, source content or exception details.
- Do not place release/preflight business rules in the protocol adapter.

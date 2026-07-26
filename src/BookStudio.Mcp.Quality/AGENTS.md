# BookStudio.Mcp.Quality Agent Rules

## Allowed

- Act as a bounded MCP protocol adapter and composition root.
- Reuse lifecycle, JSON-RPC models, tool definitions, cursors and stdio transport from `BookStudio.Mcp`.
- Invoke only deterministic provider-neutral Application quality use cases.
- Publish bounded schemas, profile resources and structured read-only results.
- Compose the Artifact Store lazily from a canonical workspace root.
- Return safe stable quality check identifiers and gate reasons.

## Forbidden

- Do not call models, OpenCode, prompts, networks or shell processes.
- Do not propose or apply repairs in this slice.
- Do not advertise or dispatch reserved repair or memory tools.
- Never mutate drafts, approvals, memory, locks or gate state.
- Never write banners, payloads, draft text or diagnostics to stdout.
- Do not expose physical paths, secrets, full draft text or exception details.
- Do not move quality business rules from Application into the protocol adapter.

# BookStudio.Tests.PromptsResources Agent Rules

## Allowed

- Launch all five real bounded MCP child processes.
- Verify initialize capability, prompts/list, prompts/get and prompt-resource parity through stdio JSON-RPC.
- Use disposable missing workspaces to prove prompt discovery is lazy and data-independent.
- Verify malformed, missing, extra, scope and unknown-prompt inputs.
- Preserve stdout-only JSON-RPC, bounded stderr diagnostics and clean EOF.

## Forbidden

- Do not invoke prompt catalogs or dispatchers directly as the sole external proof.
- Do not call models, sampling, networks, tools or data runtimes while retrieving prompts.
- Do not weaken resource parity, cursor, argument, lazy-workspace or leak assertions.
- Do not persist test workspaces or print prompt arguments as diagnostics.

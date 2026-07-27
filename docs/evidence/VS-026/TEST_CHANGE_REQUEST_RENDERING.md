# VS-026 — TestChangeRequest TCR-026-003

## Trigger

The governance contract required `McpPromptMessage` and `McpTextContent` type names to appear directly in `McpPromptDispatcher.cs`. The implemented separation assigns distinct responsibilities:

- `McpPromptDispatcher` validates JSON-RPC list/get parameters, cursor, prompt name and bounded string arguments;
- `VersionedMcpPrompt.Render` constructs the typed `McpGetPromptResult`, `McpPromptMessage` and `McpTextContent` response from one immutable prompt definition.

Duplicating message construction in the dispatcher would split prompt/resource parity and bypass the prompt's bounded renderer.

## Approved governance change

The static contract will verify:

1. `McpPromptDispatcher.cs` contains `prompts/list`, `prompts/get`, `InvalidParams`, `McpCursorCodec`, argument parsing and `McpGetPromptResult` dispatch;
2. `VersionedMcpPrompt.cs` contains `McpPromptMessage`, `McpTextContent`, bounded rendering and canonical resource generation;
3. the subprocess conformance test proves the final `prompts/get` response shape for all five servers.

## Preserved requirements

- strict `prompts/list` and `prompts/get` validation;
- typed user-role text messages;
- maximum rendered length;
- prompt/resource definition parity;
- no sampling, tool execution, model invocation or egress;
- invalid arguments remain JSON-RPC `-32602`;
- all five real MCP processes remain covered.

## Test Auditor decision

**APPROVED** — this change corrects source-location coupling while preserving and strengthening externally observable coverage.

# VS-020 — RetroSpec

## Implemented contract

`BookStudio.Mcp` is a stateful MCP stdio server that implements JSON-RPC 2.0 initialization, protocol-version negotiation, ping, lifecycle transition and graceful shutdown. It exposes no optional server feature yet.

## Supported versions

Ordered newest first:

1. `2025-11-25`;
2. `2025-06-18`;
3. `2025-03-26`;
4. `2024-11-05`.

Negotiation rules:

- supported requested version → echo it;
- unknown requested version → answer `2025-11-25`;
- the client decides whether to continue when it cannot support the server response.

## Stdio contract

- Encoding: UTF-8 without BOM.
- Input and output messages are newline-delimited.
- Embedded newline framing is not supported.
- Maximum line size after UTF-8 measurement: 1 MiB.
- stdout contains MCP JSON-RPC only.
- stderr contains safe diagnostic codes only.
- Every response is flushed immediately.
- EOF or cancellation closes the state and returns exit code 0.

## Session contract

```text
Created
→ InitializeResponded
→ Ready
→ Closed
```

- `initialize` is accepted only in Created.
- `notifications/initialized` transitions InitializeResponded to Ready.
- `ping` is accepted in any open state.
- Requests other than ping before Ready return `-32002`.
- Unknown requests in Ready return `-32601`.
- Notifications never receive responses.
- Unknown notifications are ignored with a safe diagnostic.
- Request IDs are unique within the session.

## JSON-RPC contract

Supported error mappings:

- `-32700` Parse error;
- `-32600` Invalid Request;
- `-32601` Method not found;
- `-32602` Invalid params;
- `-32603` Internal error;
- `-32002` Server not initialized.

Request IDs:

- string: 1–128 characters;
- integer: signed 64-bit JSON integer;
- null, boolean, decimal, object and array are invalid;
- invalid/unreadable IDs are returned as JSON null in protocol errors.

Arrays and JSON-RPC batches are rejected. Client responses are not accepted by this server baseline because no server-to-client request capability exists yet.

## Initialize contract

Required params:

- protocolVersion string;
- capabilities object;
- clientInfo object with name and version;
- optional clientInfo title.

Response:

```json
{
  "protocolVersion": "<negotiated>",
  "capabilities": {},
  "serverInfo": {
    "name": "bookstudio",
    "title": "BookStudio MCP",
    "version": "<assembly version>"
  },
  "instructions": "BookStudio MCP lifecycle is initialized. No tools, resources or prompts are exposed in this foundation slice."
}
```

The empty capabilities object is normative for VS-020.

## Validation and limits

- JSON max depth: 64.
- Trailing commas and comments: rejected.
- Method length: 1–128 safe characters.
- Protocol version length: 1–32 safe characters.
- Implementation name/version: 1–128 safe characters.
- Implementation title: 1–256 safe characters.
- Diagnostic code: max 96 ASCII alphanumeric/underscore/hyphen characters.
- Raw requests, params, tokens and exception details are never written to stderr.

## Architecture contract

- `BookStudio.Mcp.Protocol`: wire records, versions, serializer and session lifecycle.
- `BookStudio.Mcp.Transport`: stdio framing and safe diagnostics.
- `Program.cs`: encoding, cancellation and composition only.
- Domain and Application contain no MCP wire types.
- Initialization does not call Infrastructure.

## CI contract

Contract ID: `dotnet.mcp-initialize-integration`.

The independent executable launches the real MCP child process and verifies:

```text
malformed JSON
→ batch
→ pre-init ping
→ pre-init rejected request
→ invalid initialize
→ initialize current
→ duplicate initialize
→ initialized notification
→ ready ping
→ method not found
→ unknown notification
→ invalid id
→ EOF
```

Additional processes verify legacy negotiation, unknown-version fallback and oversize input.

## Follow-on constraints

- VS-021 may add book-core tools/resources only by extending the capability object and conformance journey together.
- No future component may write logs or banners to MCP stdout.
- New request methods must use the existing JSON-RPC error mapping and ID preservation.
- Server-to-client requests require their own pending-request correlation and timeout contract.
- Streamable HTTP is not implied by this stdio implementation.
- A future SDK migration must retain the subprocess conformance evidence and all lifecycle semantics.
- The 1 MiB check occurs after line materialization; pre-allocation enforcement belongs to security/conformance hardening.

## Next slice

`VS-021 — book-core server`.

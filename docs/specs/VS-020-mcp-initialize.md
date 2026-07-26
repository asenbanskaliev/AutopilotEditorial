# VS-020 — MCP Initialize and Stdio Lifecycle

## IntentSpec

### Problem

`BookStudio.Mcp` is currently a console placeholder that writes human text to stdout. This violates the MCP stdio transport, cannot negotiate a protocol version and provides no lifecycle boundary before future tools, resources or prompts are exposed.

### Objective

Implement the MCP base protocol and initialization lifecycle over stdio, with deterministic version negotiation, minimal capability advertisement, strict JSON-RPC validation, protocol-only stdout and safe graceful shutdown.

## Protocol baseline

- Current stable MCP protocol: `2025-11-25`.
- Supported stable revisions, newest first:
  - `2025-11-25`;
  - `2025-06-18`;
  - `2025-03-26`;
  - `2024-11-05`.
- Wire format: JSON-RPC 2.0.
- Encoding: UTF-8 without BOM.
- stdio framing: exactly one JSON-RPC object per line; embedded newlines are forbidden.
- stdout contains only valid MCP JSON-RPC messages.
- diagnostics may use stderr but must not echo request payloads.

No third-party MCP SDK is required in this slice. The adapter implements only the stable lifecycle surface with `System.Text.Json`; later server-feature slices may adopt or wrap an SDK through a separately audited migration.

## BehaviorSpec

### Lifecycle states

```text
Created
  -- initialize request --> InitializeResponded
  -- notifications/initialized --> Ready
  -- EOF/cancellation --> Closed
```

- `initialize` is accepted only in `Created`.
- `notifications/initialized` transitions only from `InitializeResponded` to `Ready`.
- Notifications never receive a response.
- Duplicate initialize requests return a protocol error.
- Requests other than `ping` before the initialize response return `Server not initialized`.
- Requests other than `ping` after the initialize response but before `notifications/initialized` also return `Server not initialized`.
- EOF closes the process successfully.

### Version negotiation

The client sends `protocolVersion` in `initialize.params`.

- If the requested version is supported, the server echoes it.
- Otherwise, the server responds with the latest supported version, `2025-11-25`.
- The response includes no speculative compatibility claim beyond the selected version.
- Supported versions are immutable ordered constants and use `YYYY-MM-DD` identifiers.

### Initialize request validation

Required request fields:

- `jsonrpc` exactly `2.0`;
- non-null string or integer `id`;
- `method` exactly `initialize`;
- object `params`;
- non-empty bounded `protocolVersion`;
- object `capabilities`;
- object `clientInfo` with bounded non-empty `name` and `version`.

Optional `clientInfo.title` is accepted when bounded. Unknown client fields are ignored. Request IDs are preserved exactly as string or integer JSON values.

### Initialize response

The successful response contains:

- negotiated `protocolVersion`;
- empty server `capabilities` object;
- `serverInfo`:
  - name: `bookstudio`;
  - title: `BookStudio MCP`;
  - version: assembly informational/version value;
- bounded instructions stating that the lifecycle foundation is active and no tool, resource or prompt capability is exposed yet.

An empty capability object is intentional. VS-020 must not advertise tools, resources, prompts, logging, completions, tasks or experimental features before their canonical slices.

### Ping

`ping` is accepted as a request in every non-closed state and returns an empty result object. It has no side effects and does not advance lifecycle state.

### JSON-RPC validation and errors

Standard errors:

- `-32700` — Parse error;
- `-32600` — Invalid Request;
- `-32601` — Method not found;
- `-32602` — Invalid params;
- `-32603` — Internal error.

Server lifecycle error:

- `-32002` — Server not initialized.

Rules:

- Arrays/batches are rejected as `Invalid Request`; initialization is never processed in a batch.
- A request ID must be string or integer and must not be null, boolean, object or array.
- Notifications must not contain `id`.
- Responses received from the client are invalid for this server-only baseline.
- Unknown requests in `Ready` return `Method not found`.
- Unknown notifications are ignored safely and logged only by method name.
- Error data may contain safe bounded protocol metadata, never raw payloads.

### Stdio transport

- Read one line at a time from stdin.
- Reject an empty line as an invalid request.
- Maximum encoded line length: 1 MiB.
- Serialize one compact JSON object followed by one newline.
- Flush each response.
- Never write banners, status messages or logs to stdout.
- stderr diagnostics contain only safe event codes/method names and are capped.
- Cancellation and EOF terminate without a JSON-RPC shutdown message.

### Security and limits

- Maximum method length: 128 characters.
- Protocol version, implementation name/version/title and instructions are bounded.
- JSON depth is bounded.
- Input payload is never included in exceptions or stderr.
- Parsing exceptions are mapped to stable protocol errors.
- Unexpected exceptions return `Internal error` only when a readable request ID exists; details remain on safe stderr.
- No filesystem, database, network or editorial operation is reachable from the lifecycle handler.

## Architecture

- `BookStudio.Mcp.Protocol` owns JSON-RPC and initialize records, version constants and session state.
- `BookStudio.Mcp.Transport` owns newline-delimited stdio I/O.
- `Program.cs` is the composition root only.
- Domain and Application remain independent of MCP protocol types.
- Infrastructure is not called during initialization.

## TDD Dual

### RED-I

The baseline lacks protocol records, version negotiation, lifecycle state, stdio server and independent CI contract. It writes non-MCP text to stdout.

### RED-E

No subprocess can prove initialize, notification transition, ping, parse/validation errors, negotiation or clean EOF.

### GREEN-I

Static protocol contracts, solution build, architecture and governance pass.

### GREEN-E

A real child process communicates over stdin/stdout and proves:

- stdout protocol purity;
- malformed JSON parse error;
- batch rejection;
- invalid request IDs;
- pre-initialize rejection;
- current, legacy and unknown version negotiation;
- empty server capabilities and stable identity;
- duplicate initialize rejection;
- initialized notification transition;
- ping before and after initialization;
- method-not-found after ready;
- no response to notifications;
- secret payload not echoed to stderr;
- clean EOF and exit code 0.

## Audit M

- M1: lifecycle, framing, negotiation and capability contract.
- M2: protocol/transport separation and composition root.
- M3: real subprocess conformance and adversarial inputs.
- M4: protocol-only stdout, limits, safe diagnostics and no premature capabilities.
- M5: launch → initialize → initialized → operation → EOF.

## Definition of Done

- SPEC_READY.
- DUAL_RED_CONFIRMED.
- DUAL_GREEN.
- NO_ORPHANS_PASS.
- M_AUDIT_PASS.
- RETROSPEC_SYNCED.

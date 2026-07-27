# VS-031 — Session lifecycle

## Status

`SPECIFICATION`

## Objective

Create a provider-neutral, bounded and compatibility-gated OpenCode session lifecycle that can create a session, read it, enqueue a text prompt asynchronously, inspect normalized session status and abort explicitly without exposing provider DTOs or invoking unrelated OpenCode capabilities.

## Dependency

`VS-030 — OpenCode compatibility` must be `VERIFIED`.

Every lifecycle instance receives an `IOpenCodeCompatibilityProbe` and refuses mutating session operations unless the latest successful compatibility report contains:

```text
health
sessions.create
sessions.get
sessions.status
sessions.prompt_async
sessions.abort
```

Event/SSE features are not required until VS-032.

## Official transport baseline

```text
POST /session
GET  /session/{id}
GET  /session/status
POST /session/{id}/prompt_async
POST /session/{id}/abort
```

Expected transport behavior:

- create returns a session JSON object;
- get returns one session JSON object;
- status returns a JSON object keyed by session ID;
- async prompt returns HTTP 204;
- abort returns a JSON boolean.

No delete, patch, shell, command, share, file or provider/model execution endpoint belongs to this slice.

## Application contract

Application owns:

- `IOpenCodeSessionLifecycle`;
- immutable commands and results;
- normalized session/status models;
- validation limits;
- stable error codes;
- idempotency semantics.

Application must not reference:

- `HttpClient`, HTTP status codes or headers;
- URI or JSON DOM types;
- provider request/response DTOs;
- Basic auth or credentials;
- environment, process, filesystem or database APIs.

## Public use cases

```text
CreateSessionAsync
GetSessionAsync
GetStatusesAsync
SendPromptAsync
AbortSessionAsync
```

### Create session

Input:

```text
parentSessionId? : bounded session ID
title?           : bounded human-readable title
idempotencyKey   : required bounded opaque key
```

Output:

```text
OpenCodeSession
```

Rules:

- title and parent are optional;
- empty optional values are rejected rather than normalized silently;
- the idempotency key is required;
- no provider or model selection is sent;
- no workspace path is sent in this slice.

### Get session

Input:

```text
sessionId : required bounded session ID
```

Output:

```text
OpenCodeSession
```

This operation is read-only and is not placed in the idempotency ledger.

### Get statuses

Input: none.

Output:

```text
sorted dictionary<sessionId, OpenCodeSessionStatus>
```

Status normalization:

```text
idle
busy
retry(attempt, message, nextUnixMilliseconds)
unknown(providerType)
```

Unknown status types are retained as bounded sanitized values and never treated as idle or completed.

### Send prompt asynchronously

Input:

```text
sessionId       : required bounded session ID
parts           : one or more bounded text parts
idempotencyKey  : required bounded opaque key
```

Output:

```text
OpenCodePromptSubmission
```

Rules:

- only text parts are allowed in this slice;
- at least one non-empty text part is required;
- part count, individual bytes and aggregate bytes are bounded;
- provider/model/agent/command/file/image/tool parts are excluded;
- HTTP 204 is the only successful provider result;
- successful submission records the idempotency entry.

### Abort session

Input:

```text
sessionId : required bounded session ID
```

Output:

```text
OpenCodeAbortResult
```

Rules:

- abort is always explicit;
- no timeout, cancellation or status observation may be converted into an implicit abort;
- provider `true` means accepted;
- provider `false` means not accepted and remains a successful bounded response, not a fabricated exception.

## Session model

The provider-neutral session projection contains only fields required by later orchestration:

```text
id
parentId?
title?
createdUnixMilliseconds?
updatedUnixMilliseconds?
```

Rules:

- ID is mandatory, bounded and sanitized;
- optional strings are bounded and control-character free;
- optional timestamps are non-negative integers;
- provider-only metadata is discarded;
- unknown extra JSON properties are ignored within the global response bound.

## Validation limits

Initial bounded defaults:

```text
session ID          <= 128 UTF-8 bytes
idempotency key     <= 128 UTF-8 bytes
title               <= 512 UTF-8 bytes
prompt part count   <= 64
text part            <= 64 KiB UTF-8
aggregate prompt     <= 256 KiB UTF-8
request JSON         <= 512 KiB
response JSON        <= 1 MiB
status entries       <= 10000
status message       <= 2048 UTF-8 bytes
unknown status type  <= 64 UTF-8 bytes
```

Every input must be non-empty where required, valid UTF-16, free of control characters except line breaks and tabs in prompt text, and validated before compatibility or network calls.

## Compatibility gate

The lifecycle may cache one successful compatible session-feature report for its own process lifetime.

Rules:

- input validation occurs before the compatibility probe;
- the first valid operation invokes compatibility detection;
- only a report with valid health and all five required session features opens the gate;
- failed/degraded reports are not cached;
- concurrent first calls share one gate evaluation;
- compatibility failure emits no session mutation request;
- cancellation of one caller must not corrupt the gate for later callers.

Safe failure codes:

```text
opencode_unavailable
opencode_authentication_required
opencode_unhealthy
opencode_session_features_missing
```

## Idempotency

Create and async-prompt commands require local process-lifetime idempotency.

Ledger key:

```text
operation + idempotencyKey
```

Fingerprint:

- deterministic SHA-256 over the canonical validated command;
- does not include credentials or endpoint URL.

Rules:

- first call reserves the key;
- concurrent duplicate calls share one in-flight task;
- same key plus same fingerprint returns the recorded result without another provider mutation;
- same key plus different fingerprint fails with `idempotency_conflict` before HTTP;
- failed or cancelled provider calls release the reservation so a later retry may execute;
- successful create records the returned session;
- successful prompt records the submission result;
- ledger is bounded by entry count and process lifetime;
- durable/restart idempotency belongs to later Autopilot/outbox slices.

## HTTP adapter

The adapter reuses `OpenCodeEndpointOptions` and Basic-auth behavior from VS-030.

Transport sequence:

```text
validate Application command
→ ensure compatibility gate
→ acquire/resolve idempotency reservation when applicable
→ serialize bounded JSON request
→ send one exact endpoint request
→ require exact success status/content type
→ stream bounded response
→ parse strict required fields
→ map to Application result
→ complete or release idempotency reservation
```

Allowed methods and paths:

```text
POST /session
GET  /session/{escaped-id}
GET  /session/status
POST /session/{escaped-id}/prompt_async
POST /session/{escaped-id}/abort
```

The session ID is escaped as one path segment. It may not introduce `/`, `\`, `?`, `#`, dot-segments or encoded path separators.

## Request bodies

Create request contains only present optional fields:

```json
{
  "parentID": "...",
  "title": "..."
}
```

Async prompt request contains text parts only:

```json
{
  "parts": [
    { "type": "text", "text": "..." }
  ]
}
```

No model, provider, agent, system prompt, tools or command is sent.

Abort and get/status requests contain no body.

## Response handling

- create/get require JSON object and a valid session ID;
- status requires JSON object and is capped by entry count;
- prompt requires exact HTTP 204 and no response buffering;
- abort requires JSON boolean;
- redirects are never followed by the owned client;
- non-success status maps to a stable safe code;
- response body is never placed in an exception or report;
- malformed JSON, invalid required fields or oversized payloads fail closed.

Stable adapter error codes include:

```text
session_not_found
session_http_status
session_payload_invalid
status_http_status
status_payload_invalid
prompt_http_status
abort_http_status
abort_payload_invalid
response_too_large
request_timeout
connection_failed
idempotency_conflict
idempotency_capacity_exceeded
```

## Security and operations

- no automatic retry;
- caller cancellation propagates;
- internal timeout maps to `request_timeout`;
- at most one lifecycle HTTP operation follows a successful compatibility gate per public call;
- no credentials, endpoint, request body, response body or prompt text in exceptions/evidence;
- Basic Authorization is added only when configured;
- JSON serialization is deterministic for idempotency fingerprints;
- the ledger has a configured maximum entry count;
- no request logs contain prompts or titles;
- all public results are provider-neutral.

## TDD Dual

### RED-I

Governance requires missing:

- Application session port/contracts/validation;
- adapter client and bounded idempotency ledger;
- integration project and contractual server;
- solution, architecture and CI registrations.

### RED-E

The real loopback HTTP journey must prove:

- compatibility refusal without mutation;
- create and get mapping;
- create idempotent replay and conflict;
- concurrent duplicate create emits one POST;
- async prompt exact 204;
- prompt idempotent replay/conflict;
- status idle/busy/retry/unknown normalization and stable sorting;
- abort true and false;
- Basic auth without leakage;
- invalid IDs/titles/parts before HTTP;
- request and response bounds;
- status entry limit;
- malformed payloads;
- timeout and caller cancellation;
- failed idempotent call can retry;
- no delete/patch/shell/command/share/file requests;
- exact request inventory.

## Gates

```text
SPEC_READY
∧ DUAL_RED_CONFIRMED
∧ DUAL_GREEN
∧ SESSION_CREATE_PASS
∧ ASYNC_PROMPT_PASS
∧ SESSION_STATUS_PASS
∧ SESSION_ABORT_PASS
∧ IDEMPOTENCY_PASS
∧ AUTH_AND_BOUNDARIES_PASS
∧ NO_UNPLANNED_MUTATION_PASS
∧ NO_ORPHANS_PASS
∧ M_AUDIT_PASS
∧ RETROSPEC_SYNCED
```

## Explicit exclusions

- SSE/event reconciliation;
- synchronous prompt response streaming;
- provider/model/agent selection;
- tool, command, shell, file or image parts;
- session delete/share/fork/revert/summarize;
- OpenCode process launch or upgrade;
- durable idempotency across restart;
- scheduler, worker retry or outbox ownership;
- persistence of prompt text or provider messages.

Those belong to subsequent slices.

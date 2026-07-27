# VS-031 — Session lifecycle

## Status

`VERIFIED`

## Objective

Provide a provider-neutral, compatibility-gated and bounded OpenCode lifecycle for:

```text
create session
get session
get session statuses
submit text prompt asynchronously
abort session explicitly
```

The adapter must not expose provider DTOs or invoke unrelated OpenCode capabilities.

## Dependency

`VS-030 — OpenCode compatibility` is `VERIFIED`.

Before the first valid lifecycle operation, the client requires a compatibility report with `healthy=true` and:

```text
health
sessions.create
sessions.get
sessions.status
sessions.prompt_async
sessions.abort
```

SSE features are not required until VS-032.

## Transport baseline

```text
POST /session
GET  /session/{id}
GET  /session/status
POST /session/{id}/prompt_async
POST /session/{id}/abort
```

Expected success:

- create/get/status/abort: HTTP 200;
- prompt_async: HTTP 204;
- abort body: JSON boolean.

No delete, patch, shell, command, share, file, provider or model endpoint belongs to this slice.

## Application boundary

Application owns:

- `IOpenCodeSessionLifecycle`;
- immutable commands/results;
- normalized session/status models;
- input and byte limits;
- stable error codes;
- idempotency semantics.

Application contains no HTTP, URI, JSON DOM, credential or provider DTO type.

## Public operations

```text
CreateSessionAsync(OpenCodeCreateSessionCommand)
GetSessionAsync(sessionId)
GetStatusesAsync()
SendPromptAsync(OpenCodeSendPromptCommand)
AbortSessionAsync(sessionId)
```

## Create session

Input:

```text
parentSessionId?
title?
idempotencyKey
```

Body includes only present `parentID` and `title`. No provider, model, agent, directory, tools or permissions are injected.

## Get session

Input is one validated session ID. HTTP 404 maps to `session_not_found`.

Provider-neutral result:

```text
id
parentId?
title?
createdUnixMilliseconds?
updatedUnixMilliseconds?
```

Unknown provider metadata is discarded.

## Statuses

The adapter returns an ordinally sorted dictionary keyed by validated session ID.

Normalized values:

```text
idle
busy
retry(attempt, message, nextUnixMilliseconds)
unknown(providerType)
```

Unknown bounded types remain unknown; they are never interpreted as idle or completed.

## Async prompt

Input:

```text
sessionId
one or more text parts
idempotencyKey
```

Body:

```json
{
  "parts": [
    { "type": "text", "text": "..." }
  ]
}
```

Only text parts are supported. HTTP 204 means accepted, not completed.

## Abort

Abort is explicit. JSON `true` means accepted and `false` means not accepted. Timeout, cancellation or a status value may never be converted into an implicit abort.

## Validation limits

```text
session ID          <= 128 UTF-8 bytes
idempotency key     <= 128 UTF-8 bytes
title               <= 512 UTF-8 bytes
prompt part count   <= 64
text part            <= 64 KiB UTF-8
aggregate prompt     <= 256 KiB UTF-8
request JSON         <= 512 KiB default
response JSON        <= 1 MiB default
status entries       <= 10000
status message       <= 2048 UTF-8 bytes
unknown status type  <= 64 UTF-8 bytes
```

Required values are non-empty and valid Unicode. IDs contain only ASCII letters, digits, `_` and `-`. Prompt text permits CR/LF/tab and rejects other control characters.

Validation happens before compatibility or network access.

## Compatibility gate

- one successful report may be cached for the lifecycle instance;
- failed/degraded reports are not cached unless `healthy=true` and all five session features are present;
- concurrent first calls share one gate evaluation;
- cancellation does not mark the gate compatible;
- compatibility failure emits no session mutation.

Safe compatibility failures:

```text
opencode_unavailable
opencode_authentication_required
opencode_unhealthy
opencode_session_features_missing
```

## Idempotency

Create and async prompt require process-lifetime idempotency.

```text
ledger key = operation + idempotencyKey
fingerprint = SHA-256(canonical validated JSON)
```

Rules:

- first caller reserves the key;
- concurrent same-key/same-fingerprint calls share one operation;
- completed same-key/same-fingerprint calls replay the recorded result without HTTP;
- same key with another fingerprint returns `idempotency_conflict` before HTTP;
- failed or cancelled operations remove the reservation;
- a later retry may execute;
- ledger capacity is bounded and returns `idempotency_capacity_exceeded`;
- durable restart-safe idempotency belongs to later Autopilot/outbox slices.

## HTTP adapter

The adapter reuses `OpenCodeEndpointOptions` from VS-030.

Requirements:

- owned client disables redirects;
- Basic Authorization is emitted only when configured;
- only GET and POST are used;
- session ID is escaped as one path segment after restrictive validation;
- request JSON is canonical and bounded;
- `ResponseHeadersRead` and streaming byte limits are mandatory;
- no automatic retry;
- caller cancellation propagates;
- internal timeout maps to `request_timeout`;
- connection/I/O maps to `connection_failed`;
- bodies, prompts, endpoint and credentials never appear in errors/evidence.

Stable operation errors include:

```text
session_not_found
session_http_status
session_payload_invalid
status_http_status
status_payload_invalid
prompt_http_status
abort_http_status
abort_payload_invalid
request_too_large
response_too_large
request_timeout
connection_failed
idempotency_conflict
idempotency_capacity_exceeded
```

## Real journey

`tests/BookStudio.Tests.OpenCodeSessionLifecycle` uses a real loopback `TcpListener` server and verifies:

- compatibility refusal without mutation;
- create/get mapping;
- create replay/conflict/concurrent collapse;
- async prompt exact 204;
- prompt replay/conflict;
- idle/busy/retry/unknown status normalization;
- abort true/false;
- Basic auth and no secret leakage;
- invalid inputs before HTTP;
- response bounds and malformed responses;
- timeout and caller cancellation;
- failed reservation release and retry;
- exact method/path inventory;
- absence of delete, patch, shell, command, share and file requests.

Verified result:

```text
OPENCODE_SESSION_LIFECYCLE_PASS scenarios=19 requests=50 mutations=15 gate=NO_UNPLANNED_MUTATION
```

## TDD Dual

### RED-I

Application port/contracts/validation, HTTP adapter, idempotency ledger, integration project, architecture and CI were absent.

### RED-E

No real HTTP executable proved lifecycle, idempotency, auth, bounds, cancellation or mutation inventory.

### GREEN

Build, architecture, Governance, accumulated journeys and the session lifecycle journey pass.

## TestChangeRequest

`TCR-031-001` moved static checks to exact ownership locations and typed constants. No observable journey requirement was weakened.

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
- prompt or provider message persistence.

## Next slice

`VS-032 — SSE reconciliation`.

The full program remains `NOT_READY`.

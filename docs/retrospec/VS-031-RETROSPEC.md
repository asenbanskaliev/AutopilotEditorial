# VS-031 — RetroSpec

## Implemented contract

BookStudio now owns a provider-neutral, compatibility-gated OpenCode session lifecycle:

```text
BookStudio.Application.OpenCode
→ BookStudio.OpenCode
→ bounded OpenCode HTTP session endpoints
```

It creates and reads sessions, submits text prompts asynchronously, reads normalized statuses and aborts explicitly.

## Public boundary

```text
IOpenCodeSessionLifecycle
```

Operations:

```text
CreateSessionAsync
GetSessionAsync
GetStatusesAsync
SendPromptAsync
AbortSessionAsync
```

Application contains no HTTP, JSON DOM, URI, credentials or provider DTOs.

## Compatibility prerequisite

Before the first valid lifecycle operation, the adapter invokes `IOpenCodeCompatibilityProbe` and requires:

```text
health
sessions.create
sessions.get
sessions.status
sessions.prompt_async
sessions.abort
```

Rules:

- validation occurs before compatibility/network access;
- only a report with `healthy=true` and all required features opens the gate;
- a successful gate is cached for the lifecycle instance;
- degraded/failed reports are not cached;
- concurrent first calls share one compatibility evaluation;
- missing compatibility never emits a session mutation.

## Session contract

Provider-neutral projection:

```text
id
parentId?
title?
createdUnixMilliseconds?
updatedUnixMilliseconds?
```

Required ID is validated. Optional fields are bounded. Unknown provider metadata is discarded.

Provider session JSON currently maps:

```text
id
parentID
title
time.created
time.updated
```

## Create contract

Input:

```text
parentSessionId?
title?
idempotencyKey
```

Transport:

```text
POST /session
```

Body contains only present optional fields. No provider, model, agent, directory, tools or permissions are injected.

Expected response: HTTP 200 JSON session object.

## Get contract

Transport:

```text
GET /session/{escaped-session-id}
```

- ID is restricted to ASCII letters, digits, `_` and `-`;
- 404 maps to `session_not_found`;
- HTTP 200 JSON session is required.

## Async prompt contract

Input:

```text
sessionId
one or more OpenCodeTextPart
idempotencyKey
```

Transport:

```text
POST /session/{escaped-session-id}/prompt_async
```

Body:

```json
{
  "parts": [
    { "type": "text", "text": "..." }
  ]
}
```

Only text parts are supported. Exact HTTP 204 means accepted. It does not mean the model run completed.

## Status contract

Transport:

```text
GET /session/status
```

Normalized statuses:

```text
idle
busy
retry(attempt, message, nextUnixMilliseconds)
unknown(providerType)
```

The returned dictionary is ordinally sorted. Unknown bounded provider types are retained and never converted to idle/completed.

## Abort contract

Transport:

```text
POST /session/{escaped-session-id}/abort
```

Expected response: HTTP 200 JSON boolean.

- `true`: accepted;
- `false`: not accepted;
- timeout/cancellation/status never implies abort.

## Input limits

```text
session ID          128 UTF-8 bytes
idempotency key     128 UTF-8 bytes
title               512 UTF-8 bytes
prompt part count   64
text part            64 KiB
aggregate prompt     256 KiB
request JSON         512 KiB default
response JSON        1 MiB default
status entries       10000
status message       2048 bytes
unknown status type  64 bytes
```

Required values are non-empty and valid Unicode. IDs cannot introduce path syntax. Prompt permits CR/LF/tab but rejects other control characters.

## Idempotency contract

Applies to create and async prompt.

```text
ledger key = operation + idempotencyKey
fingerprint = SHA-256(canonical validated JSON)
```

Behavior:

- first caller reserves;
- concurrent same key/fingerprint shares one in-flight operation;
- completed same key/fingerprint replays result without HTTP;
- same key/different fingerprint returns `idempotency_conflict` before HTTP;
- failed or cancelled operation removes reservation;
- later retry may execute;
- bounded entry capacity returns `idempotency_capacity_exceeded`;
- successful entries persist for process lifetime.

Durable restart-safe idempotency remains a later Autopilot/outbox responsibility.

## HTTP behavior

- owned client disables redirects;
- only GET and POST are used;
- `ResponseHeadersRead` is required;
- request and response bytes are bounded;
- Basic auth reuses VS-030 endpoint options;
- caller cancellation propagates;
- internal timeout maps to `request_timeout`;
- connection/I/O maps to `connection_failed`;
- no automatic retry;
- bodies, prompts, credentials and endpoint URLs are absent from exceptions/evidence.

## Allowed request inventory

```text
GET  /global/health
GET  /doc
POST /session
GET  /session/{id}
GET  /session/status
POST /session/{id}/prompt_async
POST /session/{id}/abort
```

No delete, patch, shell, command, share, file or other mutation is permitted.

## Integration journey

Project:

```text
tests/BookStudio.Tests.OpenCodeSessionLifecycle
```

It runs a real loopback socket HTTP server and records methods, paths, headers and bodies.

Verified result:

```text
OPENCODE_SESSION_LIFECYCLE_PASS scenarios=19 requests=50 mutations=15 gate=NO_UNPLANNED_MUTATION
```

The journey verifies compatibility refusal, create/get, concurrent idempotency, prompt 204, status normalization, abort true/false, auth, bounds, malformed payloads, timeout, external cancellation and retry after failed reservation.

## CI contract

```text
dotnet.opencode-session-lifecycle-integration
```

Normalized evidence:

```text
artifacts/ci/dotnet-opencode-session-lifecycle-integration.json
```

## TestChangeRequest

`TCR-031-001` aligns static assertions with exact path suffixes, authentication ownership and typed Application error constants. Functional expectations remain unchanged.

## Deviations

- Create/get/status/abort baseline expects HTTP 200; prompt_async expects HTTP 204.
- Idempotency is process-lifetime, not durable.
- Prompt completion and generated output are not returned by this slice.
- Only text prompt parts are supported.
- Session list is detected by VS-030 but not exposed by this lifecycle interface.
- Status polling is implemented, but later reconciliation must also use SSE.

## Follow-on constraints

- VS-032 must combine SSE events with status polling/recovery and preserve unknown statuses.
- Later workflow code must not interpret prompt acceptance as completion.
- Later role/model policy must extend commands explicitly; it may not inject hidden provider/model selection here.
- Durable retries must coordinate with outbox/worker and may not bypass this idempotency fingerprint.
- No future optimization may weaken validation or add unplanned endpoints.

## Phase result

`VS-031` enables `VS-032 — SSE reconciliation`. The full program remains `NOT_READY`.

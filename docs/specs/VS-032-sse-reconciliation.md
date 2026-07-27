# VS-032 — SSE reconciliation

## Status

`SPECIFICATION`

## Objective

Provide a provider-neutral, bounded and cancellation-safe OpenCode event reconciler that combines project SSE, global SSE and read-only session-status polling to produce one monotonic stream of normalized events without executing any remote mutation.

## Dependency

`VS-031 — Session lifecycle` is `VERIFIED`.

The reconciler requires a VS-030 compatibility report containing:

```text
health
events.project
events.global
sessions.status
```

It does not require create, prompt or abort features and must not call those endpoints.

## Official transport baseline

```text
GET /event
GET /global/event
GET /session/status
```

Project stream behavior:

- content type is `text/event-stream`;
- the first dispatched data event must normalize to `server.connected`;
- subsequent data events are OpenCode bus events shaped as `{ type, properties }`;
- comments/heartbeats keep the connection alive but do not produce product events.

Global stream behavior:

- content type is `text/event-stream`;
- each data event is shaped as `{ directory, payload: { type, properties } }`;
- directory is optional in the provider-neutral result and must be bounded/sanitized.

Polling behavior:

- `GET /session/status` returns a bounded object keyed by session ID;
- polling is a repair/snapshot mechanism, never the only completion signal;
- polling occurs on initial connection, reconnect and stream discontinuity;
- optional periodic polling is allowed only at a bounded configured interval.

## Application boundary

Application owns:

- `IOpenCodeEventReconciler`;
- watch request and normalized event contracts;
- event source/scope identifiers;
- reconciliation reason identifiers;
- stable terminal error codes.

Application must not reference:

- `HttpClient`, HTTP status or headers;
- URI, JSON DOM, StreamReader or channel types;
- Basic auth or credentials;
- provider SDK event unions;
- filesystem, process or persistence APIs.

## Public use case

```text
IAsyncEnumerable<OpenCodeReconciledEvent> WatchAsync(
    OpenCodeEventWatchRequest request,
    CancellationToken cancellationToken)
```

Watch request:

```text
scope: project | global | both
sessionIdFilter?: bounded session ID
```

Rules:

- default scope is both;
- optional session filter applies to normalized session-scoped events and polling output;
- infrastructure events such as `server.connected` remain visible even with a session filter;
- invalid input fails before compatibility or HTTP;
- one invocation owns all stream/poll/background resources and releases them when enumeration ends or is cancelled.

## Normalized event

```text
sequence                 local positive monotonic Int64
source                   project | global | poll
kind                     connected | session_status | provider_event | reconciliation
providerType             bounded event type
providerEventId?         bounded SSE id
sessionId?               validated session ID
directory?               bounded provider directory
status?                  OpenCodeSessionStatus
synthetic                 boolean
reconciliationReason?    initial | reconnect | eof | stall | malformed | periodic
observedUnixMilliseconds non-negative
```

Rules:

- sequence starts at 1 per Watch invocation;
- no raw provider body is exposed;
- unknown event types are retained as `provider_event` with bounded `providerType`;
- known `session.status` events normalize their session ID and status;
- malformed known events fail the current connection and trigger bounded reconciliation/reconnect;
- unknown properties are ignored;
- provider timestamps are not trusted as local observation time.

## SSE framing

`OpenCodeSseParser` accepts bounded UTF-8 SSE frames:

- LF and CRLF line endings;
- comments beginning with `:`;
- fields `event`, `data`, `id`, `retry`;
- multiple `data` lines joined by `\n`;
- one blank line dispatches one event;
- unknown fields are ignored;
- an event without data is not dispatched;
- final unterminated event at EOF is not dispatched;
- UTF-8 BOM is allowed only at stream start;
- malformed UTF-8 fails the connection;
- NUL in id is rejected;
- `retry` is accepted only as a bounded non-negative integer.

Initial defaults:

```text
maximum line bytes           16 KiB
maximum data bytes/event     256 KiB
maximum field count/event    256
maximum event type bytes     128
maximum event id bytes       256
maximum directory bytes      2048
maximum provider type bytes  128
JSON max depth               64
```

Bounds apply while reading, not after unbounded buffering.

## Stream connection

Each SSE request:

- uses GET only;
- uses `Accept: text/event-stream`;
- uses Basic Authorization only when configured;
- disables redirects in the owned handler;
- uses `ResponseHeadersRead`;
- requires HTTP 200 and `text/event-stream`;
- has a bounded header/connect timeout;
- uses a separate stall timeout for bytes/lines after headers;
- never buffers the complete stream;
- never retries through `HttpClient` handlers.

Project and global streams may run concurrently. They publish internal records through one bounded channel. Producers must wait when the channel is full; dropping events silently is forbidden.

## Compatibility gate

- input validation happens first;
- one successful report may be cached for the reconciler instance;
- `healthy=true` and all four required features are mandatory;
- failed/degraded reports are not cached;
- concurrent first watches share one compatibility evaluation;
- compatibility failure opens no SSE or polling request;
- cancellation does not mark the gate successful.

Safe codes:

```text
opencode_unavailable
opencode_authentication_required
opencode_unhealthy
opencode_event_features_missing
```

## Event normalization

Project event JSON:

```json
{
  "type": "session.status",
  "properties": {
    "sessionID": "ses_...",
    "status": { "type": "busy" }
  }
}
```

Global event JSON:

```json
{
  "directory": "/bounded/provider/value",
  "payload": {
    "type": "session.status",
    "properties": {
      "sessionID": "ses_...",
      "status": { "type": "idle" }
    }
  }
}
```

Known status variants reuse VS-031 normalization:

```text
idle
busy
retry(attempt, message, nextUnixMilliseconds)
unknown(providerType)
```

`server.connected` is normalized as `connected`. The first project data event must be this type; otherwise the stream is considered malformed and reconciled.

## Deduplication

Deduplication is process-local to one Watch invocation.

Primary key:

```text
source + providerEventId
```

Fallback key when id is absent:

```text
SHA-256(source + directory + exact bounded data payload)
```

Rules:

- project and global sources have separate namespaces;
- same key is emitted once;
- dedupe storage is bounded by configured capacity;
- oldest key is evicted deterministically when capacity is reached;
- heartbeats/comments do not enter dedupe storage;
- poll synthetic events are deduped by current session status state, not SSE keys;
- dedupe never changes local sequence for suppressed events.

## Status reconciliation

The coordinator maintains the last normalized status per session.

A status poll is requested:

- after the project stream emits `server.connected`;
- after the global stream connects;
- after EOF;
- after stall timeout;
- after malformed SSE/event payload;
- after HTTP reconnect;
- at optional periodic interval.

Poll result handling:

- new or changed status emits synthetic `session_status` with source `poll`;
- unchanged status emits nothing;
- a session absent from the snapshot is not inferred deleted or idle;
- session filter is applied before synthetic emission;
- poll errors do not fabricate state; they participate in bounded reconnect failure accounting;
- SSE status updates update the same status cache and suppress equivalent poll output.

## Reconnect and backoff

Initial defaults:

```text
initial delay              100 ms
maximum delay              5 s
multiplier                 2
maximum consecutive faults 8 per stream
stall timeout              60 s
bounded channel capacity   256
bounded dedupe capacity    4096
periodic poll              disabled by default
```

Rules:

- delay is deterministic; random jitter is not required in this slice;
- successful dispatch resets consecutive fault count and delay;
- comments/heartbeat reset stall timer but do not reset fault count unless the stream was successfully established;
- cancellation interrupts read, delay and poll immediately;
- after maximum consecutive faults, the watch terminates with a safe error code;
- reconnect sends no mutation;
- each discontinuity requests reconciliation before or immediately after the next established stream;
- no recursive reconnect implementation.

## Lifecycle and task ownership

A Watch invocation owns:

- at most two stream pump tasks;
- one bounded channel;
- one coordinator/enumerator;
- linked cancellation sources;
- dedupe and status caches.

On consumer cancellation, early disposal, terminal error or normal completion:

- linked cancellation is signalled;
- HTTP streams and responses are disposed;
- producers complete;
- channel completes exactly once;
- all tasks are awaited;
- no background task survives the enumerator;
- `DisposeAsync` of the reconciler does not dispose externally supplied clients.

## Security and operations

- only three GET endpoint patterns are permitted;
- no POST/PUT/PATCH/DELETE;
- no response body, SSE data, directory, prompt, endpoint or credential appears in exceptions/evidence;
- event IDs/types/directories/session IDs are bounded and sanitized;
- raw event JSON is never returned;
- Basic auth is applied to project, global and polling requests;
- status and dedupe caches are bounded;
- backoff and polling cannot create an unbounded request storm;
- no automatic HTTP retry handler;
- terminal errors use stable codes.

Stable codes include:

```text
sse_http_status
sse_content_type_invalid
sse_line_too_large
sse_event_too_large
sse_field_limit_exceeded
sse_utf8_invalid
sse_payload_invalid
sse_project_handshake_invalid
sse_stalled
sse_reconnect_exhausted
status_http_status
status_payload_invalid
response_too_large
request_timeout
connection_failed
```

## TDD Dual

### RED-I

Governance requires missing:

- Application reconciler contracts;
- bounded SSE parser;
- event normalizer;
- deduplicator;
- status parser shared with VS-031;
- stream/reconciliation adapter;
- integration project and contractual SSE server;
- solution, architecture and CI registration.

### RED-E

A real loopback streaming journey must prove:

- fragmented LF/CRLF parsing;
- multi-line data and comments;
- UTF-8/line/event/field bounds;
- project `server.connected` handshake;
- global wrapper normalization;
- session.status normalization;
- unknown event/status preservation;
- event-id and payload-fingerprint dedupe;
- EOF reconnect;
- malformed-event reconnect;
- stall reconnect;
- bounded backoff and exhaustion;
- initial/reconnect/gap polling repair;
- SSE/poll state suppression;
- Basic auth on all GETs;
- session filter;
- consumer cancellation and early disposal;
- no leaked tasks;
- exact GET-only request inventory.

## Gates

```text
SPEC_READY
∧ DUAL_RED_CONFIRMED
∧ DUAL_GREEN
∧ SSE_PARSE_PASS
∧ PROJECT_STREAM_PASS
∧ GLOBAL_STREAM_PASS
∧ RECONNECT_PASS
∧ POLL_RECONCILIATION_PASS
∧ DEDUPLICATION_PASS
∧ AUTH_AND_BOUNDARIES_PASS
∧ NO_MUTATION_PASS
∧ NO_LEAKED_TASKS_PASS
∧ NO_ORPHANS_PASS
∧ M_AUDIT_PASS
∧ RETROSPEC_SYNCED
```

## Explicit exclusions

- sending prompts or aborting sessions;
- message/part content persistence;
- permission/question response workflows;
- durable event offsets across process restart;
- multi-node consumer coordination;
- WebSocket transport;
- generic webhook delivery;
- UI event rendering;
- model/provider/tool execution;
- automatic session completion decisions based on one event.

Those belong to later slices.

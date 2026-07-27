# VS-032 — RetroSpec

## Implemented contract

BookStudio now reconciles OpenCode project/global events and current session status through one provider-neutral asynchronous stream:

```text
VS-030 compatibility
→ bounded project/global SSE
→ normalization and deduplication
→ status polling repair
→ monotonic reconciled events
```

The implementation is read-only and does not create sessions, send prompts, abort work or invoke models.

## Public boundary

```text
IOpenCodeEventReconciler
```

Use case:

```text
WatchAsync(OpenCodeEventWatchRequest, CancellationToken)
```

Request:

```text
scope: project | global | both
sessionIdFilter?: validated session ID
```

Application owns only provider-neutral contracts, sources, kinds, reconciliation reasons and safe errors. It contains no HTTP, JSON DOM, URI, channel, credential or provider SDK type.

## Compatibility prerequisite

Before opening any stream or polling request, the reconciler requires:

```text
health
events.project
events.global
sessions.status
```

Rules:

- validation happens before compatibility/network access;
- only `healthy=true` with every required feature opens the gate;
- successful compatibility may be cached per reconciler instance;
- failed/degraded reports are not accepted;
- compatibility failure sends no event or status request.

## Allowed HTTP inventory

```text
GET /global/health
GET /doc
GET /event
GET /global/event
GET /session/status
```

No POST, PUT, PATCH or DELETE belongs to this slice.

## SSE parser

`OpenCodeSseParser` is incremental and bounded.

Supported framing:

- LF and CRLF;
- UTF-8 BOM only at stream start;
- comments/heartbeats beginning with `:`;
- `event`, `data`, `id` and `retry` fields;
- multiple `data` lines joined by LF;
- blank-line dispatch;
- unknown fields ignored;
- incomplete final event at EOF discarded.

Failure behavior:

- malformed UTF-8 → `sse_utf8_invalid`;
- oversized line → `sse_line_too_large`;
- oversized event data → `sse_event_too_large`;
- excessive fields → `sse_field_limit_exceeded`;
- invalid bounded field → `sse_payload_invalid`;
- read silence beyond the configured bound → `sse_stalled`.

Default limits:

```text
line bytes              16 KiB
event data bytes       256 KiB
fields per event            256
event type bytes            128
event ID bytes              256
stall timeout                60 s
```

More restrictive positive event-data limits are valid.

## Project stream

Transport:

```text
GET /event
Accept: text/event-stream
```

The first dispatched project data event must be:

```text
server.connected
```

A different first event terminates that connection as malformed and enters bounded reconciliation/reconnect.

## Global stream

Transport:

```text
GET /global/event
Accept: text/event-stream
```

Provider shape:

```json
{
  "directory": "...",
  "payload": {
    "type": "...",
    "properties": {}
  }
}
```

The directory is bounded and normalized; raw JSON is not returned.

## Normalized event

```text
sequence
source: project | global | poll
kind: connected | session_status | provider_event | reconciliation
providerType
providerEventId?
sessionId?
directory?
status?
synthetic
reconciliationReason?
observedUnixMilliseconds
```

Properties:

- sequence starts at 1 for each watch and increases strictly;
- provider bodies and unknown properties are not exposed;
- unknown event types remain bounded `provider_event` records;
- infrastructure events remain visible with a session filter;
- session-scoped records respect the optional filter.

## Session status normalization

Known values:

```text
idle
busy
retry(attempt, message, nextUnixMilliseconds)
unknown(providerType)
```

`OpenCodeSessionStatusParser` is shared by VS-031 lifecycle polling and VS-032 reconciliation.

Unknown status types remain unknown and are never interpreted as idle, completion or success.

## Deduplication

One watch owns a bounded `OpenCodeEventDeduplicator`.

Primary key:

```text
source + provider event ID
```

Fallback:

```text
SHA-256(source + directory + exact bounded data payload)
```

Rules:

- project and global use separate namespaces;
- duplicate events are suppressed before sequence allocation;
- FIFO eviction is deterministic at capacity;
- heartbeats do not consume dedupe entries;
- poll results use the shared status cache instead of SSE keys.

## Polling repair

`GET /session/status` is requested around connection/discontinuity events.

A snapshot may emit a synthetic session-status event only when the observed state is new or changed.

Rules:

- equivalent SSE and poll states are suppressed;
- absence from a snapshot does not imply deletion or idle;
- polling does not reconstruct every missed intermediate event;
- polling provides the current observable status after connect, EOF, malformed input, stall and reconnect;
- polling failure fabricates no state.

## Reconnect behavior

Defaults:

```text
initial delay                100 ms
maximum delay                  5 s
multiplier                       2
maximum consecutive faults       8
```

Rules:

- reconnect is iterative, not recursive;
- delay is deterministic and cancellation-aware;
- successful dispatch resets fault count and delay;
- EOF, malformed framing/payload, stall and connection failure request reconciliation;
- exhaustion returns `sse_reconnect_exhausted`;
- no HTTP retry handler is installed.

## Concurrency and lifecycle

One watch owns:

- up to two SSE pumps;
- one optional periodic polling trigger;
- one bounded channel;
- one coordinator/enumerator;
- linked cancellation;
- bounded dedupe and status caches.

On cancellation, early enumerator disposal, terminal error or normal completion:

```text
cancel linked lifetime
→ dispose responses and streams
→ stop producers
→ complete channel once
→ await all owned tasks
```

The contractual journey verifies zero active server connections after disposal.

## Authentication

The reconciler reuses `OpenCodeEndpointOptions`.

When Basic auth is configured, the header is applied to:

- health;
- OpenAPI discovery;
- project SSE;
- global SSE;
- status polling.

Credentials, Authorization values, endpoint URLs and event bodies do not appear in normalized events or successful evidence.

## Integration journey

Project:

```text
tests/BookStudio.Tests.OpenCodeSseReconciliation
```

It uses a real loopback `TcpListener` server and the real parser, normalizer, deduplicator, status parser and reconciler.

Verified result:

```text
OPENCODE_SSE_RECONCILIATION_PASS scenarios=12 requests=52 events=27 gate=NO_MUTATION tasks=NO_LEAKED_TASKS
```

Covered behavior:

- fragmented framing, LF/CRLF, BOM, comments and multi-line data;
- strict bounds and invalid UTF-8;
- project handshake;
- global wrapper;
- known/unknown status preservation;
- ID and fingerprint deduplication;
- EOF, malformed and stall reconnect;
- reconnect exhaustion;
- polling repair and unchanged-status suppression;
- Basic auth;
- session filter;
- cancellation and early disposal;
- exact GET-only request inventory.

## CI contract

```text
dotnet.opencode-sse-reconciliation-integration
```

Normalized evidence:

```text
artifacts/ci/dotnet-opencode-sse-reconciliation-integration.json
```

## TestChangeRequests

`TCR-032-001` aligns static checks with executable/auth ownership.

`TCR-032-002` waits for both independently asynchronous EOF outcomes: repaired status in history and a second accepted stream connection. It does not require duplicate unchanged state.

## Deviations and residual constraints

- dedupe and status history are process-local;
- durable offsets across restart are not implemented;
- deterministic backoff has no jitter;
- unknown event properties are intentionally discarded;
- polling guarantees current observable status, not the complete missed event sequence;
- this slice does not determine editorial completion or success.

## Follow-on constraints

- later orchestration must consume the provider-neutral stream rather than provider JSON;
- durable recovery must persist its own checkpoint/outbox state without weakening bounded parsing;
- multi-instance deployment must add jitter/coordination before synchronized reconnect at scale;
- completion logic must combine explicit workflow evidence and may not infer success from a single idle event;
- no later optimization may add mutation endpoints to this reconciler.

## Phase result

`VS-032` completes bounded OpenCode event/status reconciliation. The full program remains `NOT_READY` until the remaining execution-plan slices are verified.

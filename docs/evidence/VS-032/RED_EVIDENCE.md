# VS-032 — RED Evidence

## Scope

SSE reconciliation is intentionally absent before implementation.

Required observable flow:

```text
compatibility gate
→ project/global SSE connection
→ bounded SSE parse
→ event normalization and dedupe
→ status cache update
→ polling repair on connect/discontinuity
→ bounded reconnect/backoff
→ cancellation-safe shutdown
```

## RED-I — Missing implementation contracts

The initial branch does not contain:

- `IOpenCodeEventReconciler`;
- provider-neutral watch/event/source/reason contracts;
- bounded SSE parser;
- event normalizer;
- bounded deduplicator;
- reusable session-status parser;
- HTTP stream/reconciliation client;
- integration project and contractual SSE server;
- solution, architecture and CI registration.

Governance must fail until all components exist and are linked.

## RED-E — Missing observable journey

No executable currently proves:

- LF/CRLF and fragmented SSE parsing;
- comments/heartbeats and multi-line data;
- UTF-8, line, field and event bounds;
- project `server.connected` handshake;
- global `{directory,payload}` normalization;
- session status event normalization;
- unknown event/status preservation;
- id/fingerprint deduplication;
- EOF, malformed and stall reconnect;
- bounded backoff/exhaustion;
- initial and reconnect polling repair;
- SSE/poll duplicate suppression;
- Basic auth on both streams and polling;
- session filtering;
- early enumerator disposal and no leaked tasks;
- exact GET-only inventory.

## Expected initial gates

```text
Plan Integrity = PASS
Governance Gates = FAIL
.NET CI may remain PASS because incomplete code is not registered yet
```

The Governance failure is the required RED-I signal.

## Independence

- Governance statically checks contracts, bounds and permanent wiring.
- The external journey must run a real loopback HTTP/SSE server.
- Tests must use the real parser, reconciler and HTTP adapter.
- No mock of `IOpenCodeEventReconciler` may satisfy GREEN-E.
- Polling repair must use real HTTP `GET /session/status`.

## Repair policy

Any expectation change after RED requires a documented `TestChangeRequest` demonstrating that no framing, reconnect, reconciliation, security, lifecycle or request-inventory requirement was weakened.

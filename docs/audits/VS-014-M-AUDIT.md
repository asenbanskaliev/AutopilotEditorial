# VS-014 — Auditoría M

## Resultado

`PASS`

## Governance correction

The canonical backlog row is `VS-014 — API and health`. The Outbox audit previously stored under this path has been reclassified as preimplementation evidence for future VS-040 and no longer contributes slice credit.

## M1 — Specification

- Liveness, readiness, diagnostics, correlation, Problem Details and binding policy are explicit.
- `/health/live` has no durable dependency.
- `/health/ready` reflects required probes with 200/503 semantics.
- Diagnostics are operationally useful without exposing machine-specific or sensitive data.

## M2 — Implementation

- Application owns a provider-neutral readiness probe contract and sanitized result.
- Infrastructure maps workspace database health into a stable readiness status without paths or exception messages.
- ControlCenter owns host options, DI composition, HTTP routes, correlation and startup behavior.
- SQLite initialization failure is caught by the hosted initializer so process liveness remains available.
- The default URL is loopback-only and remote binding requires an explicit opt-in.
- Existing `/health` behavior remains as a compatibility alias.

## M3 — Tests

The real Kestrel integration executable proves:

- remote/wildcard binding rejected by default;
- ephemeral loopback host startup;
- liveness 200 with correlation ID;
- compatibility `/health` 200;
- healthy readiness 200;
- diagnostics 200 and ready status;
- unknown route 404 with `application/problem+json`;
- safe incoming correlation ID preserved in response and Problem Details;
- overlong correlation ID replaced;
- failed SQLite initialization does not break liveness;
- unhealthy readiness returns 503;
- unhealthy diagnostics remain sanitized;
- clean host stop and disposal.

All architecture, SQLite, Artifact Store and early Outbox regression journeys remain GREEN.

## M4 — Security and Operations

- Bind validation uses absolute HTTP/HTTPS URLs and rejects non-loopback hosts unless explicitly enabled.
- Correlation values are printable and limited to 128 characters.
- Responses do not include workspace paths, connection strings, passwords, secrets, exception messages or stack traces.
- Readiness failures use stable states: `missing`, `unhealthy`, `error`.
- Problem Details includes service and correlation metadata, not raw exceptions.
- Database initialization logs only the exception type.
- Tests use port zero and no fixed port or external network.

Residual risk: enabling remote binding only changes the listen policy; authentication and remote authorization remain out of scope and must be completed before any non-loopback production exposure.

## M5 — Product Flow

```text
start host
→ initialize storage or retain not-ready state
→ /health/live
→ /health/ready
→ /api/v1/diagnostics
→ RFC 7807 error response
→ clean shutdown
```

## Meta-Audit

- The master backlog was not edited to hide the earlier misclassification.
- `VS-014` was restored from VERIFIED to IN_PROGRESS before canonical implementation.
- Outbox code remains active regression-tested code but is registered as `PREIMPLEMENTED_NOT_CERTIFIED` for VS-040.
- The first API GREEN attempt failed compilation; the harness import was fixed without reducing expectations.
- No sensitive field was added solely to satisfy diagnostics.
- No external testing package was introduced; Kestrel is exercised through the real composition root.

## Evidence

- Canonical RED Governance run: `30213773241`, job `89824299853`.
- Failed build run: `30214103948`, job `89825144591`.
- GREEN .NET run: `30214216020`, job `89825424558`.
- GREEN Governance run: `30214216024`.
- GREEN Plan Integrity run: `30214216073`.
- Evidence artifact: `8635329386`.
- Digest: `sha256:253ae9a658b954498ace790495e45e3b99435ad6d6d5ae7629591b592720dd88`.

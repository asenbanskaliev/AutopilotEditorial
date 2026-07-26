# VS-014 — API and Health

## IntentSpec

### Problem

The foundation has durable storage and background capabilities but no stable local HTTP contract for operators, installers or the future Control Center UI. A single generic `/health` response cannot distinguish process liveness from dependency readiness and exposes no safe diagnostics.

### Objective

Provide a versioned local API foundation with separate liveness/readiness, sanitized diagnostics, correlation IDs, Problem Details and secure loopback binding by default.

### Governance correction

PR #15 implemented an Outbox capability under the wrong slice identity. That implementation remains active regression-tested code but is registered as `PREIMPLEMENTED_NOT_CERTIFIED` for future `VS-040`. It does not satisfy this specification.

## BehaviorSpec

### Binding

- Default URL: `http://127.0.0.1:5074`.
- Remote or wildcard binding is rejected unless `ControlCenter:AllowRemoteBinding=true`.
- The configured workspace root is canonicalized but never returned by API responses.

### Correlation

- Accept `X-Correlation-ID` only when printable and at most 128 characters.
- Otherwise generate a new opaque ID.
- Return the selected ID in every response and include it in Problem Details.

### Endpoints

- `GET /health/live`: process-only liveness, independent of storage.
- `GET /health/ready`: readiness of required probes; returns 200 or 503.
- `GET /api/v1/diagnostics`: sanitized service, version, environment, uptime and probe summaries.
- `GET /health`: compatibility alias for liveness.
- Unknown routes return RFC 7807-compatible `application/problem+json`.

### Storage readiness

- SQLite initialization runs during host startup but initialization failure does not kill liveness.
- Readiness verifies database existence, WAL, foreign keys, integrity and migration state through a provider-neutral probe.
- Failure responses expose stable status codes, not exception messages, paths or connection strings.

### Diagnostics safety

Responses must not include:

- workspace or database paths;
- connection strings;
- environment variables;
- secrets, tokens or passwords;
- exception stack traces or raw messages.

### Shutdown

The host and durable database services dispose cleanly and release file handles.

## TDD Dual

### RED-I

Governance tests require host options, readiness contracts, implementation, endpoint factory and API integration project before they exist.

### RED-E

No real host journey proves healthy and unhealthy readiness, correlation, sanitized diagnostics, Problem Details or loopback policy.

### GREEN-I

Static contracts, architecture and build pass.

### GREEN-E

A real Kestrel host on an ephemeral loopback port passes all HTTP journeys in CI.

## Audit M

- M1: endpoint and status semantics match the issue.
- M2: API composition remains in ControlCenter; storage details remain Infrastructure.
- M3: healthy, unhealthy, error, correlation and binding tests.
- M4: no information disclosure and secure default bind.
- M5: start → live → ready → diagnostics → problem → stop journey.

## Definition of Done

- SPEC_READY.
- DUAL_RED_CONFIRMED.
- DUAL_GREEN.
- NO_ORPHANS_PASS.
- M_AUDIT_PASS.
- RETROSPEC_SYNCED.

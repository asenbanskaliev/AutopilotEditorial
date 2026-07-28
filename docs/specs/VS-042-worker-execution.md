# VS-042 — Worker execution

## IntentSpec

Durable scheduler jobs require a bounded execution runtime that maps a versioned job type to exactly one handler, maintains lease ownership while work progresses, enforces timeout and schedules retry without leaking background tasks.

## BehaviorSpec

- Handler registration is unique by `jobType + schemaVersion`.
- Unknown handlers fail the job with a stable bounded error.
- A worker claims a bounded batch and executes each job once under its current lease.
- Handler context exposes immutable job data and an explicit heartbeat callback.
- Heartbeat renews the lease for the same owner only.
- Every execution has a hard timeout and linked cooperative cancellation.
- Success completes the job only while the worker still owns a live lease.
- Failure or timeout schedules retry using deterministic backoff.
- Losing the lease prevents stale completion/failure from overwriting the new owner.
- All handler tasks are awaited before the worker iteration returns.
- No external network mutation is performed by the runtime itself.

## TDD Dual

- RED-I: handler registry, worker runtime and execution contracts absent.
- RED-E: no real heartbeat/timeout/retry/lease-loss journey.
- GREEN-I: build, architecture and governance pass.
- GREEN-E: cumulative scheduler journey proves execution lifecycle.

## Gates

- `HANDLER_REGISTRY_PASS`
- `HEARTBEAT_PASS`
- `TIMEOUT_PASS`
- `RETRY_PASS`
- `LEASE_LOSS_PASS`
- `NO_ORPHAN_TASKS_PASS`
- `NO_REMOTE_MUTATION_PASS`
- `DUAL_GREEN`
- `M_AUDIT_PASS`
- `META_AUDIT_PASS`
- `RETROSPEC_PASS`

# VS-041 — Scheduler and jobs

## IntentSpec

Autopilot requires a durable scheduler that selects ready jobs deterministically, grants ownership-safe leases and survives process restarts without duplicate concurrent execution.

## BehaviorSpec

- Jobs have immutable ID, type, schema version and payload.
- Creation is idempotent when immutable fields match and conflicts otherwise.
- Priority is bounded; larger values run first.
- Claim order is priority descending, availability ascending, creation ascending, ID ascending.
- Eligible jobs are queued, retryable failed, or running with an expired lease.
- Claim records owner, lease expiry and increments attempts.
- Only the live lease owner may renew, complete or fail.
- Failure clears ownership, records a bounded error and schedules retry.
- Completion is terminal.
- Expired jobs are reclaimed after restart.
- No scheduler operation performs external network mutation.

## States

`QUEUED → RUNNING → COMPLETED`

`RUNNING → FAILED → RUNNING`

Expired `RUNNING → RUNNING` through reclaim.

## TDD Dual

- RED-I: scheduler contracts, migration and implementation absent.
- RED-E: no real priority/lease/retry/restart journey.
- GREEN-I: build, architecture and governance pass.
- GREEN-E: cumulative scheduler journey passes.

## Gates

- `JOB_SCHEMA_PASS`
- `PRIORITY_ORDER_PASS`
- `LEASE_PASS`
- `RENEW_PASS`
- `RECLAIM_PASS`
- `RETRY_PASS`
- `IDEMPOTENCY_PASS`
- `NO_REMOTE_MUTATION_PASS`
- `DUAL_GREEN`
- `M_AUDIT_PASS`
- `META_AUDIT_PASS`
- `RETROSPEC_PASS`

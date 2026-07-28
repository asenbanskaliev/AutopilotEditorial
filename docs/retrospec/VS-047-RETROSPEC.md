# VS-047 RetroSpec — Concurrency limits

## Delivered

Durable hierarchical concurrency budgets for Autopilot executions, with atomic multi-scope reservation, capacity reporting, lease renewal, generation fencing, idempotent release and expired-grant recovery.

## Durable rules

- A worker receives every required scope or receives no grant.
- Every scope must have an explicit configured limit.
- Acquire identity is stable and replay-safe.
- Generation changes invalidate stale worker control messages.
- Expiry and release restore capacity but preserve the historical grant.
- Configuration versions are monotonic.
- The lease store is authoritative for scheduling ownership.

## Corrections discovered by CI

- Nullable journey assertions were made explicit before dereference.
- Acquire replay was corrected to validate immutable request content without comparing the generated grant ID against a null caller field.
- Transaction result types were made explicit to keep generic inference fail-closed.

Status: VERIFIED.

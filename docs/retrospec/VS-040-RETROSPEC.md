# VS-040 RetroSpec — Transactional Outbox

## Delivered

Autopilot state mutation, operation receipt and one or more durable Outbox envelopes are committed in one serialized SQLite transaction.

## Durable rules

- Operation IDs are stable idempotency keys.
- Replays require an identical request fingerprint.
- Failures and cancellation roll back every write in the unit of work.
- The operation receipt stores the committed state version and message IDs.
- Dispatch remains at-least-once; consumers must remain idempotent.
- Transaction bodies perform no external calls.

## Reuse

The implementation extends the established Outbox tables and dispatcher rather than creating a parallel delivery mechanism.

Status: VERIFIED.

# VS-042 RetroSpec — Worker execution

## Delivered

A bounded worker runtime that resolves exact versioned handlers, claims durable jobs, renews leases through explicit heartbeat, enforces timeout, schedules retry and rejects stale completion after lease loss.

## Durable rules

- Handler resolution is exact and duplicate registration is invalid.
- Worker iterations await all handler tasks before returning.
- Heartbeat is explicit and ownership-scoped.
- Timeout uses linked cooperative cancellation and bounded retry state.
- Losing a lease converts stale completion into an observed lease-loss result.
- Runtime remains transport-neutral and performs no network mutation itself.

## Test discipline

Lease-loss behavior is proven with two real SQLite scheduler stores: one stale worker and one recovery owner.

Status: VERIFIED.

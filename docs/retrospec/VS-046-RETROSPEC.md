# VS-046 RetroSpec — Dead letter recovery

## Delivered

A durable quarantine and recovery workflow for retry-exhausted scheduler and Outbox failures, including immutable evidence, versioned repair, deterministic requeue identity and terminal discard.

## Durable rules

- Exhausted failures become inspectable dead letters.
- Original failure evidence is never overwritten by repair data.
- Repair, requeue and discard use attributable idempotent request identities.
- Requeue only follows a successful repair and emits one durable recovery intent.
- Discard never deletes the record.
- Terminal records reject further mutation.
- Recovery state survives restart.

Status: VERIFIED.

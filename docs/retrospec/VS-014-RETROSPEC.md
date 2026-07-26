# VS-014 — RetroSpec

## Implemented contract

The platform now has provider-neutral domain events and a durable SQLite Outbox with explicit at-least-once semantics.

## State and identity

- `MessageId` is the idempotency key.
- Immutable fields: event type, schema version, payload JSON, occurrence time and initial availability.
- Legal states: `PENDING`, `PROCESSING`, `FAILED`, `PROCESSED`.
- Claims increment attempts and create a bounded lease owned by one worker.

## Durable behavior

- Identical enqueue returns `AlreadyExists`.
- Reusing a message ID with changed immutable data throws a conflict.
- Claims are ordered by availability, creation and message ID.
- Live leases exclude other workers and store instances.
- Completion and failure require the current live lease owner.
- Failure clears ownership, records a bounded diagnostic and schedules retry.
- Expired processing leases are reclaimed after restart.
- Cross-store concurrent claim produces one winner.

## Migration contract

`0002_outbox.sql` creates the durable table, state/lock constraints and dispatch index. The SQLite baseline tests now calculate expected migration count and latest version from `SqliteMigrationCatalog`, while retaining all previous migration-integrity checks.

## Delivery semantics

The Outbox guarantees durable at-least-once dispatch, not exactly-once external side effects. Every future consumer must use `MessageId` as an idempotency key and tolerate redelivery after crash windows.

## Operational contract

- Payloads are valid bounded JSON.
- Worker IDs and event metadata are bounded.
- Failures retain at most 2,048 diagnostic characters.
- Time is supplied explicitly to allow deterministic lease and retry behavior.
- CI runs the Outbox journey in an independent process and emits `dotnet.outbox-integration` evidence.

## Follow-on constraints

- The dispatcher must perform external effects outside SQLite transactions.
- A dispatcher may only complete after the effect is acknowledged.
- Poison-message and dead-letter policy must be added explicitly in a later slice rather than inferred from attempts.
- Domain aggregates may collect `IDomainEvent` instances, but persistence transaction integration must preserve enqueue atomicity.
- Autopilot jobs must not reuse Outbox tables as a general job queue.

## Next slice

`VS-015` as resolved by the executable master backlog.

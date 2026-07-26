# VS-014 — Outbox and Domain Events

## IntentSpec

State-changing use cases need durable side-effect publication without calling external systems inside the primary transaction. The outbox provides explicit at-least-once delivery and crash recovery.

## BehaviorSpec

### Domain event

- `IDomainEvent` exposes immutable `EventId` and `OccurredAtUtc`.
- Event type and schema version are explicit in the outbox envelope.
- Payload is canonical JSON supplied by the application boundary; Infrastructure stores it byte-for-byte as text.

### Enqueue

- Message IDs are GUIDs.
- Enqueue is idempotent when every immutable field matches.
- Reusing an ID with different content is an explicit conflict.
- `AvailableAtUtc` defaults to enqueue time.

### Claim and lease

- Claim is atomic and ordered by availability then creation.
- Eligible messages are pending, retryable failed, or processing with an expired lease.
- A claim records worker ID, lease expiry and increments attempts.
- A live lease prevents another worker from claiming the message.
- Batch size, worker ID and lease duration are validated.

### Completion and failure

- Only the current lease owner may complete or fail a message.
- Completion is terminal and records processed time.
- Failure clears ownership, records a bounded error and schedules the next attempt.
- Expired processing messages can be reclaimed after restart.

### States

`PENDING → PROCESSING → PROCESSED`

`PROCESSING → FAILED → PROCESSING`

Expired `PROCESSING` may be claimed again.

### Scope boundaries

- No external dispatcher or message broker.
- No Autopilot job runtime.
- No domain-specific event classes.
- No exactly-once claim; consumers must be idempotent.

## TDD Dual

- RED-I: contracts, migration, implementation and CI contract are absent.
- RED-E: no real enqueue/claim/lease/fail/reclaim/restart journey exists.
- GREEN-I: static, migration and architecture checks pass.
- GREEN-E: SQLite integration proves the complete at-least-once lifecycle.

## Audit M

- M1 contract/state coherence.
- M2 transactional implementation and idempotency.
- M3 positive, conflict, ownership, lease and restart tests.
- M4 bounded input/error fields and no external calls in transactions.
- M5 end-to-end publication lifecycle.

## Definition of Done

- SPEC_READY.
- DUAL_RED_CONFIRMED.
- DUAL_GREEN.
- NO_ORPHANS_PASS.
- M_AUDIT_PASS.
- RETROSPEC_SYNCED.

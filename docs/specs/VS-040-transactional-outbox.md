# VS-040 — Transactional Outbox

## IntentSpec

Autopilot workflow mutations must never be committed without their corresponding durable integration events, and events must never be committed without the mutation that produced them.

## BehaviorSpec

- A command is identified by a stable `operationId` and immutable request fingerprint.
- A single SQLite transaction writes workflow state, operation receipt and one or more outbox envelopes.
- Replaying the same `operationId` with the same fingerprint returns the stored result without duplicating state or messages.
- Reusing the same `operationId` with a different fingerprint fails closed.
- Any exception or cancellation before commit rolls back state, receipt and outbox messages.
- Events remain at-least-once and are consumed through the existing lease-based outbox.
- Restart does not lose committed events or expose rolled-back state.
- Payloads, operation IDs and result values are bounded and validated.

## Contracts

Application:

- `ITransactionalOutboxUnitOfWork`
- `TransactionalOutboxCommand`
- `TransactionalOutboxMutation`
- `TransactionalOutboxResult`
- stable error codes for invalid input and idempotency conflict.

Infrastructure:

- `SqliteTransactionalOutboxUnitOfWork`.
- dedicated operation receipt and workflow state tables.
- reuse of the existing `outbox_messages` table and lease dispatcher contract.

## TDD Dual

- RED-I: contracts, migrations, implementation, architecture registration and CI contract absent.
- RED-E: no real atomic commit/rollback/replay/restart journey.
- GREEN-I: build, architecture and governance pass.
- GREEN-E: cumulative journey proves atomicity and crash recovery.

## Acceptance gates

- `ATOMIC_COMMIT_PASS`
- `ATOMIC_ROLLBACK_PASS`
- `IDEMPOTENCY_PASS`
- `IDEMPOTENCY_CONFLICT_PASS`
- `CANCELLATION_ROLLBACK_PASS`
- `CRASH_RECOVERY_PASS`
- `AT_LEAST_ONCE_PASS`
- `NO_REMOTE_MUTATION_PASS`
- `DUAL_GREEN`
- `M_AUDIT_PASS`
- `META_AUDIT_PASS`
- `RETROSPEC_PASS`

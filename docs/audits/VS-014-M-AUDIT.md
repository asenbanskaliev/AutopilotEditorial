# VS-014 — Auditoría M

## Resultado

`PASS`

## M1 — Specification

- The state machine, idempotency, lease ownership, retry timing and at-least-once semantics are explicit.
- Domain, Application and Infrastructure responsibilities are separated.
- Exactly-once processing is explicitly out of scope; consumers remain idempotent.

## M2 — Implementation

- `IDomainEvent` is a provider-neutral Domain marker.
- Application owns immutable Outbox records, statuses, port and domain-facing errors.
- Infrastructure owns the SQL migration, transactions, JSON validation and SQLite mapping.
- Enqueue is idempotent only when every immutable message field matches.
- Claim, complete and fail are transactional and ownership-safe.
- Attempts increment at every successful claim, including expired-lease recovery.
- Failure clears ownership, bounds diagnostics and schedules the next attempt.
- Existing SQLite tests now derive migration expectations from the canonical migration catalog.

## M3 — Tests

The real Outbox executable proves:

- invalid JSON rejection;
- insert and identical idempotent enqueue;
- conflicting reuse of message ID;
- byte-for-byte JSON payload preservation;
- ordered eligible claim;
- attempts and ownership metadata;
- live lease exclusion;
- wrong-owner completion rejection;
- successful completion;
- bounded failure error and retry scheduling;
- lease expiry and reclaim after store restart;
- delayed availability;
- invalid lease duration;
- two independent store instances racing for one message yield one claim only;
- disposed-store rejection.

All prior architecture, SQLite and Artifact Store journeys continue to pass.

## M4 — Security and Operations

- Message, event, schema and worker identifiers are validated and bounded.
- Payload must be valid JSON and is bounded to 1 MiB.
- Error diagnostics are bounded to 2,048 characters.
- No external call occurs inside the SQLite transaction.
- A live lease is required for completion or failure.
- Expired leases are recoverable after process restart.
- Migration constraints enforce legal states and lock-field consistency.
- The implementation uses the existing single-writer queue per instance and SQLite transaction boundaries; the cross-instance race is covered by integration evidence.

Residual risk: Outbox consumers may execute side effects before a crash prevents completion, so duplicate delivery remains possible by design. Consumer idempotency keys must use `MessageId`.

## M5 — Product Flow

```text
domain fact
→ immutable Outbox draft
→ idempotent enqueue
→ atomic claim + lease
→ consumer side effect
→ complete
```

On failure:

```text
PROCESSING
→ FAILED + next availability
→ later claim
→ complete
```

On crash:

```text
PROCESSING + expired lease
→ claim by recovery worker
→ attempts increment
→ complete
```

## Meta-Audit

- The old hard-coded migration count was changed through an approved TestChangeRequest, without dropping any prior assertion.
- A cross-store concurrency test was added before PASS.
- No sleeping or wall-clock dependency is used; all timestamps are explicit.
- No broker, dispatcher, job runtime or domain-specific event type was introduced early.
- No test was weakened and no existing slice was marked PASS by inference.

## Evidence

- RED Governance run: `30212896805`, job `89822025244`.
- Initial GREEN .NET run: `30213286624`, job `89823023585`.
- Hardened cross-store GREEN .NET run: `30213360913`, job `89823219952`.
- Evidence artifact: `8635099953`.
- Digest: `sha256:2852a8bb791fc703b8e66292ad3a1a560714039c4d72ce6ef0c9968f9719b807`.

# VS-046 — Dead letter recovery

## IntentSpec

Failures that exhaust retries must be quarantined durably, remain inspectable, and support controlled repair or discard without losing the original evidence or duplicating downstream work.

## BehaviorSpec

- Capture is idempotent by dead-letter ID and immutable failure fingerprint.
- Original source kind, source ID, payload, schema version, attempt count, error class, error text and timestamps are preserved.
- Quarantined records cannot be reprocessed automatically.
- A repair request records actor, reason, replacement payload, schema version and request fingerprint.
- Identical repair replay is idempotent; conflicting replay fails closed.
- Requeue emits exactly one new scheduler/outbox identity and links it to the dead letter.
- Discard is terminal, attributable and does not delete evidence.
- Requeued and discarded records cannot be repaired again.
- Restart preserves quarantine, repair and recovery receipt.
- No remote side effect occurs inside the persistence transaction.

## States

`QUARANTINED → READY_FOR_RETRY → REQUEUED`

`QUARANTINED | READY_FOR_RETRY → DISCARDED`

## Gates

- `DEAD_LETTER_SCHEMA_PASS`
- `CAPTURE_IDEMPOTENCY_PASS`
- `QUARANTINE_PASS`
- `REPAIR_PASS`
- `REQUEUE_ONCE_PASS`
- `DISCARD_PASS`
- `CONFLICT_FAIL_CLOSED_PASS`
- `RESTART_PASS`
- `NO_REMOTE_MUTATION_PASS`
- `DUAL_GREEN`
- `M_AUDIT_PASS`
- `META_AUDIT_PASS`
- `RETROSPEC_PASS`

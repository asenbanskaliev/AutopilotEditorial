# VS-046 Auditoría M

Status: PASS

- Retry-exhausted failures are quarantined instead of silently discarded.
- Original payload, schema, attempts, classification and error evidence remain immutable.
- Capture and recovery requests are idempotent by stable identity and immutable fingerprint.
- Repair is only allowed from quarantine; requeue only from repaired state.
- Requeue persists one deterministic Outbox identity in the same transaction.
- Discard is terminal, attributable and preserves forensic evidence.
- Conflicting request replay and invalid transitions fail closed.
- Restart preserves quarantine, repair state and recovery receipt.
- No remote side effect occurs inside the SQLite transaction.

Residual risk: administrative authorization and payload redaction policies must be enforced by the API/UI surfaces that expose these contracts.

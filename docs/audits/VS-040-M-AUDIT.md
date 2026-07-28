# VS-040 Auditoría M

Status: PASS

- Atomic boundary is a single SQLite transaction serialized by the workspace write queue.
- Operation-level idempotency binds immutable request fingerprint to stored state version and message IDs.
- Any exception or cancellation rolls back state, receipt and messages.
- Duplicate message IDs, invalid JSON and unbounded values fail closed.
- Committed messages retain existing ownership-safe lease and at-least-once semantics.
- No external network mutation occurs inside the transaction.

Residual risk: consumers remain responsible for idempotent side effects, as required by the at-least-once contract.

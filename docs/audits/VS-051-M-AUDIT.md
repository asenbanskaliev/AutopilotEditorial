# VS-051 Auditoría M

Status: PASS

- Discovery identity is scoped by workspace and session.
- Questions are immutable after creation and validated by declared type.
- Answers append versions instead of overwriting prior evidence.
- Decisions and open items are attributable to an actor.
- Completion fails closed while required questions or open items remain unresolved.
- Identical requests replay idempotently; conflicting immutable fingerprints fail closed.
- Completion makes the session immutable.
- Completion state and Outbox intent commit atomically.
- Restart preserves the complete discovery record.
- No remote side effect occurs inside the SQLite transaction.

Residual risk: later slices must define controlled reopening/version branching rather than mutating completed discovery evidence.

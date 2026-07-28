# VS-050 — Project journey

## IntentSpec

An editor must be able to create a new editorial project through one coherent application command and immediately retrieve the durable project that will anchor every later discovery, specification, planning and authoring step.

## BehaviorSpec

- Creation requires a stable request ID and project ID.
- Immutable initial fields are workspace ID, name, project kind, language, audience and objective.
- Creation is idempotent for identical content.
- Reusing either identity with different immutable content fails closed.
- Projects are isolated by workspace.
- State and `editorial.project.created` Outbox intent commit atomically.
- The event has a deterministic message ID and is emitted once.
- Project retrieval is available by workspace and project ID.
- Restart preserves the project and pending event.
- Input is normalized only where explicitly defined; no silent semantic rewriting occurs.
- No remote mutation occurs inside the persistence transaction.

## Initial state

`ACTIVE`

## Gates

- `PROJECT_SCHEMA_PASS`
- `CREATE_PASS`
- `IDEMPOTENCY_PASS`
- `IDENTITY_CONFLICT_PASS`
- `WORKSPACE_ISOLATION_PASS`
- `OUTBOX_ONCE_PASS`
- `READ_AFTER_WRITE_PASS`
- `RESTART_PASS`
- `NO_REMOTE_MUTATION_PASS`
- `DUAL_GREEN`
- `M_AUDIT_PASS`
- `META_AUDIT_PASS`
- `RETROSPEC_PASS`

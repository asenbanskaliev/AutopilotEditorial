# VS-045 — Pause resume cancel

## IntentSpec

Autopilot operators must be able to pause, resume and cancel workflow executions durably without corrupting scheduler ownership, duplicating work or losing auditability.

## BehaviorSpec

- Controls target a stable execution ID and include actor, reason, request ID and timestamp.
- Requests are idempotent by request ID and immutable fingerprint.
- Pause prevents new work claims and asks a running worker to stop cooperatively at a safe checkpoint.
- Resume returns a paused execution to runnable state exactly once.
- Cancel is terminal, prevents future claims and propagates cooperative cancellation to a live worker.
- Repeating the same control is idempotent; conflicting reuse fails closed.
- Invalid transitions fail closed.
- Stale worker completions after pause/cancel cannot overwrite the control state.
- State, audit receipt and Outbox control event commit atomically.
- Restart preserves execution state and pending control delivery.

## States

`RUNNABLE ↔ PAUSED`

`RUNNABLE | RUNNING | PAUSED → CANCELLED`

`CANCELLED` is terminal.

## Gates

- `CONTROL_SCHEMA_PASS`
- `PAUSE_PASS`
- `RESUME_PASS`
- `CANCEL_PASS`
- `IDEMPOTENCY_PASS`
- `INVALID_TRANSITION_PASS`
- `STALE_WORKER_PASS`
- `RESTART_PASS`
- `NO_REMOTE_MUTATION_PASS`
- `DUAL_GREEN`
- `M_AUDIT_PASS`
- `META_AUDIT_PASS`
- `RETROSPEC_PASS`

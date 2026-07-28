# VS-045 RetroSpec — Pause resume cancel

## Delivered

Durable operator control for Autopilot executions with pause, resume and terminal cancellation, idempotent request handling, versioned stale-worker protection and transactional Outbox events.

## Durable rules

- Every operator control carries request identity, actor, reason and immutable fingerprint.
- Pause blocks new claims while allowing cooperative checkpoint shutdown.
- Resume only transitions paused executions back to runnable.
- Cancel is terminal.
- State version is authoritative over stale worker completions.
- Control state and delivery intent are one transaction.
- Replayed identical commands do not duplicate events.

Status: VERIFIED.

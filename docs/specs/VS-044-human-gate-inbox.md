# VS-044 — Human gate inbox

## IntentSpec

Autopilot workflows that require human judgment must pause durably, expose an auditable decision request, accept exactly one terminal decision, and resume the blocked workflow exactly once.

## BehaviorSpec

- Gate requests have immutable request ID, workflow ID/version, step ID, job ID, prompt, schema version and expiry.
- Creation is idempotent for identical immutable content and conflicts otherwise.
- Open requests may be claimed by one actor under a bounded lease.
- Only the live claim owner may approve or reject.
- Decisions are terminal and immutable.
- Repeating the same decision is idempotent; a different decision conflicts.
- Expired open or claimed requests transition to `EXPIRED` and cannot be decided.
- Cancellation is terminal and cannot be reversed.
- A terminal approval/rejection emits one durable resume command through the transactional Outbox.
- Restart preserves requests, claims, decisions and pending resume delivery.
- No path may resume the same workflow gate more than once.

## States

`OPEN → CLAIMED → APPROVED | REJECTED`

`OPEN | CLAIMED → EXPIRED | CANCELLED`

## TDD Dual

- RED-I: contracts, migration, durable store and resume integration absent.
- RED-E: no cumulative human-gate journey proving claim, expiry, idempotency and resume-once.
- GREEN-I: build, architecture and governance pass.
- GREEN-E: real SQLite journey and Outbox resume evidence pass.

## Gates

- `GATE_SCHEMA_PASS`
- `CLAIM_PASS`
- `DECISION_PASS`
- `IDEMPOTENCY_PASS`
- `EXPIRY_PASS`
- `CANCEL_PASS`
- `RESUME_ONCE_PASS`
- `RESTART_PASS`
- `NO_REMOTE_MUTATION_PASS`
- `DUAL_GREEN`
- `M_AUDIT_PASS`
- `META_AUDIT_PASS`
- `RETROSPEC_PASS`

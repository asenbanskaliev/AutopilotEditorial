# Early Capabilities Registry

This registry records implementation that exists in the repository but does not satisfy the canonical completion state of its future master-plan slice.

## Transactional Outbox preimplementation

- Origin issue: #14.
- Origin PR: #15.
- Merged commit: `fcc129241d52a53fc11b7f83eac9faaaacf65d25`.
- Canonical target slice: `VS-040 — Transactional Outbox`.
- Current certification state: `PREIMPLEMENTED_NOT_CERTIFIED`.
- Reason: the work was incorrectly labelled as `VS-014`, while the immutable backlog defines `VS-014` as `API and health` and places Transactional Outbox after `VS-035`.

### Reusable implementation

- `IDomainEvent` contract;
- Outbox Application port and records;
- SQLite migration and store;
- idempotent enqueue;
- leases, retries and crash recovery;
- integration executable and CI evidence.

### Restrictions

- This capability does not mark `VS-014` or `VS-040` complete.
- `VS-040` must re-audit transaction coupling with authoring aggregates, dispatcher integration, idempotent consumers and dependencies available after `VS-035`.
- Existing tests remain active as regression coverage.
- No resolver may skip `VS-014` or `VS-040` because this code exists.

## Governance rule

A capability is removed from this registry only when its canonical slice is verified through its own issue, PR, dependencies, TDD dual evidence, Auditoría M and RetroSpec.

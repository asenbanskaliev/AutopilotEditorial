# VS-060 — Scene generation

## Intent

Convert every scene in one approved ScenePlan version into a durable generated scene revision with complete causal evidence, reproducible model invocation metadata and governed approval.

## Behaviors

1. Generation requires an approved ScenePlan with matching project, version, approval message and content digest.
2. Every planned scene has one stable generation aggregate identity per ScenePlan version.
3. The generation brief preserves planned scene key, chapter key, local order, purpose, summary, beats, required evidence, constraints and acceptance criteria.
4. Provider/model, prompt template version, compiled context digest, parameters and policy profile are recorded before execution.
5. Generated text is stored as an immutable revision with SHA-256 content digest.
6. Exact request replay is idempotent; conflicting request reuse fails closed.
7. A failed attempt may be superseded by a new append-only attempt without overwriting evidence.
8. Only a completed revision satisfying non-empty content and acceptance evidence may be submitted.
9. Approval emits exactly one `editorial.scene.approved` Outbox event.
10. Approved content is immutable; a new revision line must be opened for later repair.
11. State survives restart and remains isolated by workspace.
12. No remote provider call occurs inside the SQLite transaction.

## State model

`PLANNED → GENERATING → GENERATED → SUBMITTED → APPROVED`

`GENERATING → FAILED`

`FAILED | GENERATED | SUBMITTED → GENERATING` through an explicit new attempt.

## Gates

- Causal ScenePlan authority.
- Complete brief and invocation record.
- Append-only attempts and revisions.
- Content hashing and acceptance evidence.
- Idempotency and optimistic concurrency.
- Approval Outbox exactly-once.
- Restart and workspace isolation.
- DUAL_GREEN, Auditoría M, Meta-Audit and RetroSpec.

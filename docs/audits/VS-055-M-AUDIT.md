# VS-055 Auditoría M

## Verdict

PASS.

## Model

`ScenePlan` is a workspace-scoped aggregate linked immutably to one approved `BookPlan` version through project identity, approval message identity and content digest. Versions are append-only; the current version exposes only the latest revision.

## Invariants

1. No ScenePlan can be created from an unapproved or mismatched BookPlan.
2. All BookPlan chapters must be covered.
3. Scene identity and chapter-local order are unique.
4. Dependency references exist, are non-self-referential and form a DAG.
5. Only DRAFT content can be revised.
6. PREPARED and COMMITTED content cannot be edited.
7. COMMITTED content has a deterministic digest.
8. APPROVED emits exactly one durable Outbox authorization.
9. Request IDs are immutable receipts.
10. Approved history is never overwritten.

## Failure analysis

- Stale version/revision: fail closed with conflict.
- Invalid causal evidence: fail closed with validation error.
- Missing coverage or cyclic graph: fail closed before persistence.
- Replayed request: return durable prior result.
- Conflicting request replay: fail closed.
- Restart: reconstruct aggregate from SQLite history.

## Mutation boundary

No provider or remote side effect is executed within the transaction. Approval persists aggregate state, request receipt and Outbox message atomically.

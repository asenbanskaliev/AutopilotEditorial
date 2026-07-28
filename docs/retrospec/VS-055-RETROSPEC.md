# VS-055 RetroSpec

## Delivered

A complete, durable Scene Planning vertical slice now converts one approved BookPlan version into a governed, versioned scene graph.

## Confirmed decisions

- BookPlan approval identity and digest are causal inputs, not descriptive metadata.
- Chapter coverage is total; orphan chapters are invalid.
- Scene ordering is local to each chapter.
- Dependencies may cross chapters but must form a DAG.
- Content becomes immutable after PREPARE and is sealed at COMMIT.
- Approval is represented by one deterministic Outbox authorization.
- Opening a new version never mutates approved history.

## Feedback incorporated

The journey proves the entire upstream editorial chain rather than bypassing it with direct SQL fixtures. Validation remains in the durable store boundary so every caller receives identical fail-closed behavior.

## Follow-on

The next slice may create drafting work units only from `editorial.scene-plan.approved`, preserving scene identity, order, evidence requirements and acceptance criteria.

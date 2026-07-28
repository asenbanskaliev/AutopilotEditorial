# VS-062 — Scene coherence

## Intent

Audit one approved generated scene against the exact approved ScenePlan item that authorized it, proving causal continuity, planned beat coverage and the promised exit state before the scene may participate in chapter-level gates.

## Behaviors

1. Creation requires an approved scene generation with matching project, approval message, content digest and immutable generated text.
2. The exact ScenePlan version must contain the referenced scene key, chapter key, order, purpose, objective, entry state, exit state and planned beats.
3. Every planned beat receives one durable assessment: `SATISFIED`, `PARTIAL`, `MISSING` or `OUT_OF_ORDER`, with an attributable text range or explicit absence evidence.
4. Causal links are recorded from cause range to effect range; broken, reversed or unsupported causality creates findings.
5. Entry-state assumptions and exit-state claims are assessed independently and versioned.
6. Findings are append-only, rule-versioned, severity classified and resolved only by governed decisions.
7. Audit lifecycle is `DRAFT → RUNNING → REVIEWED → CLOSED`.
8. Closure is forbidden while blocking beat, causal or exit-state findings remain open.
9. Exact request replay is idempotent; conflicting reuse fails closed.
10. Optimistic concurrency, restart durability and workspace isolation are mandatory.
11. Closing emits exactly one `editorial.scene-coherence.closed` Outbox message atomically.

## Gates

- Exact approved scene and ScenePlan authority.
- Complete beat coverage with stable ranges.
- Causal and state assessments.
- Governed findings and decisions.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec and full CI.

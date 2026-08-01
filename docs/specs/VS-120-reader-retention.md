# VS-120 — Autonomous editorial intelligence and reader retention

## Intent

Create an independent, evidence-bearing reader-engagement authority that continuously evaluates scenes, chapters and complete manuscripts, identifies likely abandonment points, coordinates bounded repairs and blocks publication while unresolved engagement risks remain.

## Behaviors

1. A retention case declares workspace, project, exact approved manuscript revision, immutable reader promise, target audience, genre profile, locale, evaluator versions, actor and deterministic request fingerprint.
2. The reader promise defines expected experience, emotional trajectory, genre conventions, reading level, pacing envelope, prohibited betrayals and measurable acceptance thresholds.
3. Every scene and chapter receives deterministic scores for hook, desire, conflict, novelty, progression, exposition load, tension, payoff, emotional connection, clarity and predictability.
4. Findings are localized to manuscript, chapter, scene, paragraph or span and preserve rule, severity, evidence, confidence, expected threshold and observed value.
5. An independent critic ensemble can contribute editor, genre specialist, impatient reader, character critic, pacing critic and continuity critic assessments without any single model becoming authoritative.
6. Deterministic evidence and independent critic evidence remain distinguishable; disagreement, insufficient coverage and evaluator drift are explicit findings.
7. Risk is aggregated into a chapter-by-chapter abandonment map with stable risk bands and a manuscript publication gate.
8. Blocking risks, broken reader promises, unresolved high-risk abandonment points or insufficient evidence prevent approval.
9. Repair planning selects the smallest safe scope and a typed strategy such as hook strengthening, exposition compression, conflict escalation, payoff repair, dialogue repair, scene merge, scene cut or chapter reordering.
10. Repairs are bounded, preserve immutable source evidence and require reevaluation against the same or a superseding authority before approval.
11. Exact replay is idempotent; changed payload reuse, stale manuscript authority, cross-workspace access and conflicting evaluator versions fail closed.
12. Cases, evaluations, findings, critic votes, risk maps, repair plans, decisions, receipts, append-only history and deterministic Outbox messages are atomic and restart-safe.

## Invariants

- No approved retention case references stale, missing or digest-mismatched manuscript authority.
- No publication-ready state exists with unresolved blocking findings or high abandonment risk.
- Provider output cannot directly approve content or weaken deterministic thresholds.
- Reader promise, audience, genre, locale, manuscript revision and evaluator identities cannot be mixed.
- Exact replay cannot duplicate evaluations, repair plans, decisions or Outbox effects.
- Failed transitions expose no partially approved state.

## Initial risk bands

- LOW: weighted risk below 0.30 and no blocking finding.
- MEDIUM: weighted risk from 0.30 to below 0.55.
- HIGH: weighted risk from 0.55 to below 0.75 or one major unresolved finding.
- CRITICAL: weighted risk at or above 0.75, broken reader promise or one blocking finding.

## Gates

- Typed reader-promise, authority, metric, finding, critic, risk-map, repair, decision and state contracts.
- Deterministic metric normalization and evidence digests.
- Independent ensemble evidence with quorum and disagreement handling.
- Smallest-safe-scope repair planning and bounded reevaluation.
- Fail-closed publication gate.
- Replay, optimistic concurrency, rollback, restart recovery and workspace isolation.
- Append-only history and deterministic exactly-once Outbox.
- Dual TDD GREEN, Auditoría M, Meta-Audit, RetroSpec and complete same-head CI.

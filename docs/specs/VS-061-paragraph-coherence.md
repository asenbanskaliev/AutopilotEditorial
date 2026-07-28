# VS-061 — Paragraph coherence

## Intent

Create a durable local-coherence audit for the exact digest of an approved scene.

## Behaviors

1. Audit creation requires an approved `scene_generation` with matching project, approval message and content digest.
2. Paragraphs are segmented deterministically and stored with stable ordinal and exact character range.
3. Findings are append-only and include rule id/version, category, severity, paragraph/range, evidence and recommendation.
4. Supported local checks include continuity, reference, repetition, contradiction, clarity and flow.
5. The lifecycle is `DRAFT → RUNNING → REVIEWED → CLOSED`.
6. Closing fails while blocking findings remain unresolved.
7. Exact request replay is idempotent; conflicting reuse and stale revisions fail closed.
8. Closing emits exactly one `editorial.paragraph-coherence.closed` Outbox event.
9. State survives restart and remains isolated by workspace.

## Gates

- Exact approved-scene authority.
- Stable paragraph ranges.
- Attributable append-only findings and decisions.
- Optimistic concurrency and idempotency.
- Outbox exactly-once.
- Restart and workspace isolation.
- DUAL_GREEN, Auditoría M, Meta-Audit and RetroSpec.
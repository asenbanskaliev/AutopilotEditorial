# VS-133 — Professional Literary Quality Gates

## Objective

Apply deterministic professional literary quality gates before publication progression.

## Dimensions

Continuity, chronology, character consistency, contradictions, repetition, voice, pacing, factual risk and chapter-goal compliance.

## Decisions

- `PASS`: every dimension and the average meet policy thresholds.
- `REVISE`: correctable quality deficits remain.
- `BLOCKED`: a material blocker exists or a dimension falls below the blocking threshold.

## Controls

- Every dimension must be scored exactly once from 0 to 100.
- Evaluator decisions must equal the deterministic policy result.
- Writer, reviser and evaluator identities must be separate.
- Revision attempts are bounded.
- Each evaluation is persisted as JSON Lines evidence with manuscript hash.
- Material blockers stop immediately.
- Exhausted revision loops remain fail-closed as `REVISE`.

## Acceptance proof

The harness verifies REVISE-to-PASS improvement, immediate BLOCKED handling, maximum revision attempts, persisted evidence, manuscript hash changes and independent identities.

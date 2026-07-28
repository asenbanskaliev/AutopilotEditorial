# VS-062 RetroSpec

## What changed

The repository now supports durable scene-level coherence audits tied to one exact approved scene and ScenePlan version.

## Learned constraints

- Beat coverage must preserve planned order and exact textual evidence rather than a single pass/fail score.
- Causal links need independent ranges for cause and effect so reversed or unsupported causality remains attributable.
- Entry state, objective and exit state are separate proof obligations even when represented by common findings.
- Closure must atomically enforce all gates, persist the terminal state and emit one Outbox event.

## Follow-through

Transition and knowledge-state slices should consume the closed scene-coherence evidence instead of re-reading ungoverned generated text or introducing disconnected analyzers.

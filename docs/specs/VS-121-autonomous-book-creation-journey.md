# VS-121 — Autonomous Book Creation Journey & No-Command User Experience

## Intent

Provide one durable user journey that starts from a natural-language book idea and automatically coordinates the existing editorial authorities until a governed final package is ready, without requiring the user to execute technical commands.

## Behaviors

1. A journey starts from a normalized creation brief containing idea, audience, genre, language, target length, tone, autonomy policy, image policy, output formats, cost limits and actor evidence.
2. The journey exposes one global state and progress view while preserving exact phase-specific authority references.
3. Supported autonomy modes are `GUIDED`, `SUPERVISED` and `AUTONOMOUS`; each mode has explicit decision, repair, cost and safety boundaries.
4. The orchestrator advances automatically through intake, editorial proposal, plan, authoring, quality, reader-retention, visual production, packaging, proof and release readiness.
5. Phase completion is accepted only with the exact approved, current, digest-matched authority required by the next phase.
6. Recoverable findings create bounded repair cycles over the smallest safe scope. Exhausted retry budgets, legal ambiguity, unsafe content, budget breach or conflicting creative choices create a user decision instead of silent continuation.
7. Decisions are presented in user language with options, recommendation, impact and blocking reason; internal IDs and commands are not required from the user.
8. Pause, resume and cancellation are durable. Restart reconstructs the journey and resumes only from the last committed checkpoint.
9. Exact replay is idempotent; conflicting request reuse, stale revision, cross-workspace authority or changed policy fails closed without duplicate phase launches or Outbox effects.
10. Final readiness requires manuscript, assets, metadata, package, digital proof and any policy-required physical proof authorities to be approved and current.
11. The Control Center may project progress and decisions from the journey contract, but cannot bypass domain gates.

## Invariants

- No phase starts before its declared dependencies are approved and current.
- No user is required to execute CLI or MCP commands to continue a normal journey.
- Automatic repair never exceeds the configured attempt, cost or scope limits.
- A blocking decision prevents dependent phases from starting.
- Exact replay cannot duplicate phase execution intent, decisions or final package effects.
- Failed transitions cannot expose partially advanced journey state.
- Final readiness cannot exist with unresolved blocking findings or stale upstream authority.

## Canonical phases

`INTAKE → EDITORIAL_PROPOSAL → BOOK_PLAN → AUTHORING → EDITORIAL_QUALITY → READER_RETENTION → VISUALS → PRODUCTION_PACKAGE → PROOF → RELEASE_READY`

## Gates

- Typed creation brief, autonomy policy, phase, progress, decision, repair and authority contracts.
- Deterministic next-action planning with dependency and blocking-decision enforcement.
- Exception-only user decision queue.
- Bounded automatic repair and escalation.
- Pause/resume/cancel and restart-safe checkpoints.
- Replay, optimistic concurrency, workspace isolation, append-only history and exactly-once Outbox boundaries.
- No-command Control Center projection contract.
- Dual TDD GREEN, Auditoría M, Meta-Audit, RetroSpec and complete same-head CI.
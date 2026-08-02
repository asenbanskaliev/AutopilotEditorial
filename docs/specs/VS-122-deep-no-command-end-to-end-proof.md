# VS-122 — Deep No-Command End-to-End Book Proof

## Intent

Prove a durable application-level flow that starts from one natural-language idea, survives interruption, continues without technical commands and freezes an exact publication package containing EPUB, PDF, DOCX and KDP deliverables.

## Behaviors

1. A proof run owns one workspace-scoped identity, one VS-121 journey identity and one durable checkpoint file.
2. Checkpoints are written atomically and contain revision, phase, status, accumulated cost, repair attempts, artifact manifest and exact evidence digest.
3. Restart loads the last committed checkpoint and never replays a completed phase or duplicates an artifact.
4. Automatic repair is bounded by attempt and cost policy; exhaustion fails closed and requires a decision.
5. Final readiness requires approved journey completion plus an exact manifest containing EPUB, PDF, DOCX and KDP-package artifacts with digest, byte size, media type and provenance.
6. Artifact files are verified against the manifest before readiness is granted. Missing, changed, empty or cross-workspace artifacts fail closed.
7. The user-facing start surface accepts natural-language intent and policy only; normal continuation requires no CLI or MCP command.
8. Cancellation, unsafe content, legal ambiguity and budget breach cannot be silently bypassed.

## Invariants

- At most one committed revision exists per checkpoint write.
- A phase is executed at most once for one proof run and revision chain.
- Restart resumes from the last committed phase.
- Cost never exceeds the configured maximum.
- Repair attempts never exceed the configured maximum.
- Final readiness is impossible without all required verified artifacts.
- Evidence digest is deterministic for the exact checkpoint and manifest.

## Gates

- Typed execution, checkpoint, artifact and readiness contracts.
- Atomic file-backed checkpoint store.
- Restart-safe execution coordinator.
- Bounded repair and cost enforcement.
- Exact artifact verification and fail-closed readiness.
- Integration smoke proving interruption, restoration, continuation and final package verification.
- Dual TDD evidence, Auditoría M, Meta-Audit, RetroSpec and updated completion matrix.

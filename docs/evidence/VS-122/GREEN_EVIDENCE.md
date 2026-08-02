# VS-122 GREEN_EVIDENCE

Status: IMPLEMENTED — pending final same-head CI validation.

## Dual TDD GREEN-I

- Typed proof request and policy capture natural-language intent, workspace, journey, cost ceiling, repair ceiling and required formats.
- Atomic file-backed checkpoints use optimistic revision enforcement and write-through replacement.
- Restart reconstructs the latest committed checkpoint without duplicating completed phases.
- Automatic repair and accumulated cost are bounded; exhaustion becomes a blocking decision.
- Exact artifact manifest records format, path, media type, byte size, SHA-256, provenance and verification state.
- Publication readiness requires verified EPUB, PDF, DOCX and KDP artifacts.
- Missing, changed, empty or path-escaping artifacts fail closed.
- Terminal replay is idempotent.

## Dual TDD GREEN-E

`DeepBookProofIntegrationSmoke` is executed by the existing integration test program and proves creation, interruption, restoration, automatic continuation, exact artifact verification, terminal replay and repair exhaustion.

Final PASS requires Plan Integrity, Governance Gates and .NET CI green on the exact final PR SHA.

# VS-122 — RetroSpec

Status: IMPLEMENTED — pending same-head CI.

## What changed

The product now has an application-level proof boundary between the VS-121 journey and final publication artifacts. The proof persists every transition, survives coordinator reconstruction, enforces cost and repair policy, and verifies final bytes before readiness.

## Decisions retained

- Existing editorial authorities remain authoritative.
- Checkpoints record orchestration evidence; they do not fabricate specialist approval.
- Normal continuation is no-command and restart-safe.
- Artifact filenames are insufficient: size, digest, media type and provenance are required.
- Publication readiness requires EPUB, PDF, DOCX and KDP package verification.
- Exhausted repair or cost budgets require a blocking decision.

## Follow-up boundary

Real provider-backed generation and Amazon upload remain deployment integrations. VS-122 proves the durable governed handoff and exact package boundary without claiming external publication occurred.

# VS-121 — RetroSpec

Status: PASS pending same-head CI confirmation.

## What changed

The repository already contained specialist workflows for planning, authoring, quality, images, production, proof and release. The missing capability was not another specialist tool but a single product-level authority that determines what happens next and when the user must intervene.

## Decisions retained

- The user starts with natural-language intent, never internal command syntax.
- Existing authorities remain authoritative; the journey only coordinates them.
- Progress is global for the user but preserves exact phase evidence.
- Automatic continuation is the default when dependencies are current and no blocking decision exists.
- Human intervention is exception-only and localized.
- Repairs are bounded and escalation is mandatory after exhaustion.
- Final readiness is evidence-based, not inferred from elapsed workflow steps.

## Follow-up boundary

Provider configuration and physical-proof inspection remain deployment or human responsibilities. They cannot be fabricated by orchestration.

Conclusion: PASS subject to same-head CI.
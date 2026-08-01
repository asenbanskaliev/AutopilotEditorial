# VS-120 — RetroSpec

Status: PASS pending same-head CI confirmation.

## What changed from the initial assumption

Existing pacing, beta-reader, continuity and copyediting passes were strong but distributed. VS-120 adds a single independent publication-facing authority that consolidates engagement evidence without replacing those specialist passes.

## Decisions retained

- Reader promise is immutable authority, not a prompt hint.
- Engagement risk is evaluated per unit and aggregated manuscript-wide.
- Deterministic evidence and model/critic opinion remain distinguishable.
- High or critical risk blocks publication.
- Repairs target the smallest safe scope and require reevaluation.
- No metric can guarantee commercial success or universal reader completion.

## Operational learning

A useful autonomous editor must identify where risk occurs, explain the dominant drivers and produce bounded repair intent. A global quality score alone is not actionable and would hide localized abandonment points.

## Follow-up boundary

Future work may add genre-specific rule packs, calibrated reader telemetry and durable SQLite implementation without weakening the current authority, evidence or fail-closed invariants.

Conclusion: PASS, subject to all three required workflows succeeding on the exact final SHA.

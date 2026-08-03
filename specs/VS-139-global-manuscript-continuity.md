# VS-139 — Global Manuscript Continuity Review

## Objective
Review and repair the whole manuscript rather than isolated chapters.

## Invariants
- Chapters are contiguous and one-based.
- Reviewer, writer and repairer identities are independent.
- Chronology, character arcs, unresolved subplots, contradictions, repetition, pacing, opening-ending coherence and factual consistency are represented explicitly.
- Material blockers stop immediately.
- Repairs are limited to chapters named by review findings.
- Chapter count cannot change during repair.
- Every attempt persists a manuscript hash, findings, changed chapters and reviewer identity.
- Repair attempts are bounded and fail closed when the policy is exhausted.

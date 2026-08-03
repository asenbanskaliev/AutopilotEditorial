# VS-137 Long-Running Full Book Acceptance Test

## Objective

Prove that the production editorial system can complete a bounded realistic six-chapter book across repeated process restarts without losing or duplicating work, while preserving literary quality evidence and producing a reproducible KDP package.

## Acceptance criteria

- Six unique chapters are approved.
- The run is interrupted after chapter two and resumed from persisted state.
- Reconstructed service instances skip completed chapters and continue missing chapters.
- Every chapter records exactly one `REVISE` assessment followed by one `PASS` assessment.
- Final quality averages improve and meet the professional threshold.
- No chapter, assessment or package file is lost or duplicated.
- EPUB, print PDF, metadata, checklist and manifest are generated.
- Rebuilding after another restart produces identical package and manifest hashes.
- Sanitized evidence contains no credential canary.
- Exact-head CI is green before merge.

## Bounded execution

The harness uses six chapters, three process-boundary reconstructions and a maximum of two quality assessments per chapter. It performs no unbounded retries and has a twenty-minute workflow timeout.

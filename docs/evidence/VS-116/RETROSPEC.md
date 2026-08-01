# VS-116 RetroSpec

## What the implementation clarified

1. Package determinism requires governing both manifest serialization and ZIP metadata; stable file ordering alone is insufficient.
2. Metadata completeness must be represented as explicit findings rather than silently defaulting publication-sensitive values.
3. The immutable evidence boundary includes upstream authority, profile versions, normalized metadata, artifact digests, findings, manifest digest and package digest.
4. Evaluation and approval are separate state transitions so blocking findings cannot be bypassed by package construction.
5. Exact replay and optimistic concurrency belong to every mutation, not only initial submission.

## Specification refinements retained

- Profile and marketplace versions remain explicit governed inputs.
- Artifact content is read-only and verified before use.
- Approved package changes require a superseding revision.
- External KDP acceptance is not inferred; VS-116 produces a deterministic governed package for downstream proofing.

## Follow-through

The next dependency-ready slice must consume only an approved VS-116 package authority and verify its package/evidence digests without mutating the frozen package.

# VS-114 RetroSpec

## What the implementation clarified

- Accessibility approval is a governed production authority, not a transient analyzer result.
- Automated findings and manual-review evidence must coexist in one immutable evidence model.
- Analyzer identity, version, rule profile and input/output digests are part of the domain contract because reproducibility depends on them.
- Waivers require bounded scope, expiry, approver and evidence; they cannot silently downgrade findings.

## Specification refinements retained

- Exact current approved VS-113 authority is required for analyze, review and decision operations.
- Approval fails closed while any blocking finding remains open or required manual review is incomplete or failed.
- Exact replay is idempotent; conflicting replay and stale revisions fail.
- Every authoritative mutation persists state, normalized evidence, receipt, history and deterministic Outbox atomically.
- Approved evidence is immutable; later changes require a superseding run revision.

## Follow-through for the next slice

VS-115 must consume only the exact approved VS-114 accessibility authority and must not infer approval from analyzer output, mutable working state or historical evidence. It should preserve workspace, project, run revision and evidence digest identity end to end.

## Completion rule

This RetroSpec records learned constraints but does not assert PASS. VS-114 is complete only when the final unchanged SHA has GREEN_EVIDENCE, Auditoría M, Meta-Audit and RetroSpec, all required workflows are green, review threads are clear and the protected merge succeeds.

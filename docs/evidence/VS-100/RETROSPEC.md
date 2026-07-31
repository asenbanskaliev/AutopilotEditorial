# VS-100 RetroSpec

## What changed

VS-100 introduced a durable visual-brief lifecycle authorized by VS-095 legal-risk cases. It adds typed application contracts, SQLite schema, transactional persistence, continuity references, append-only review and transition history, persisted idempotency receipts, exact legal-authority checks, optimistic concurrency, restart-safe reads, and deterministic Outbox messages.

## Confirmed invariants

- No visual brief is created or approved without exact, approved, current VS-095 authority.
- Approval is impossible with missing mandatory brief data, unresolved blocking findings, incomplete accessibility intent, absent review evidence, or stale authority.
- Failed transitions do not leave partial briefs, continuity references, reviews, receipts, history, or Outbox messages.
- Exact replay is side-effect free; conflicting request reuse is rejected.
- Workspace, project, subject, version, legal authority, channel, and continuity boundaries cannot be crossed.
- Process restart does not change the authoritative brief, review, replay, or lifecycle result.

## Lessons retained

- Durable workflow stores must use SQLite as the read and replay authority rather than process-local dictionaries.
- Review evidence must be committed atomically with the state transition it authorizes.
- Legal-risk authority and visual-continuity evidence remain separate but revisionally and digest-linked.
- Final governance evidence must be committed before definitive same-head CI verification.

## Follow-on authority

VS-100 becomes the dependency authority for the next dependency-ready vertical slice only after final same-head green verification and merge.

# VS-095 RetroSpec

## What changed

VS-095 introduced a durable legal-risk lifecycle authorized by VS-094 provenance records. It adds typed application contracts, SQLite schema, transactional persistence, append-only findings, reviews and history, idempotency receipts, jurisdiction and policy evaluation, qualified human legal review, exact authority checks, drift handling, and cumulative integration coverage.

## Confirmed invariants

- No legal-risk case is created without exact, approved, current provenance authority.
- Approval is impossible with unresolved blocking findings, missing mandatory human review, expired review conditions, or stale authority.
- Automated evaluation cannot substitute for a required human legal decision.
- Failed transitions do not leave partial cases, findings, reviews, history, receipts, or Outbox messages.
- Exact replay is side-effect free; conflicting replay is rejected.
- Workspace, project, subject, version, provenance authority, jurisdiction, and policy boundaries cannot be crossed.

## Lessons retained

- Legal policy and jurisdiction versions must remain explicit so later changes mark cases stale rather than silently reinterpret prior decisions.
- Human legal review evidence must stay durable, scoped, attributable, conditional, and expirable.
- Provenance authority and legal-risk evidence must stay separate but cryptographically and revisionally linked.
- Final governance evidence must be committed before definitive same-head CI verification.

## Follow-on authority

VS-095 becomes the dependency authority for the next dependency-ready vertical slice only after final same-head green verification and merge.

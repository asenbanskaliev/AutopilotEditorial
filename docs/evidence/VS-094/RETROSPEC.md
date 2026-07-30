# VS-094 RetroSpec

## What changed

VS-094 introduced a durable AI provenance and disclosure lifecycle authorized by VS-093 rights records. It adds typed application contracts, SQLite schema, transactional persistence, append-only history, idempotency receipts, disclosure records, exact authority checks, drift handling, and cumulative integration coverage.

## Confirmed invariants

- No provenance record is created without exact, approved, current rights authority.
- Approval is impossible for unknown, incomplete, contradictory, non-compliant, or stale provenance.
- Failed transitions do not leave partial records, disclosures, history, receipts, or Outbox messages.
- Exact replay is side-effect free; conflicting replay is rejected.
- Workspace, project, asset, version, and authority boundaries cannot be crossed.

## Lessons retained

- Provenance policy versions must remain explicit so future policy changes can mark existing records stale rather than silently reinterpret them.
- Rights authority and provenance evidence must stay separate but cryptographically and revisionally linked.
- Final governance evidence must be committed before the definitive same-head CI verification.

## Follow-on authority

VS-094 becomes the dependency authority for VS-095 Legal risk workflow after final same-head green verification and merge.
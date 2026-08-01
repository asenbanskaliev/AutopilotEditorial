# VS-115 RetroSpec

## What changed from RED to GREEN

VS-115 began without a technical-preflight authority boundary, deterministic checker model, normalized findings, governed waivers or durable approval state. The completed implementation now binds one exact approved VS-114 authority to a reproducible checker pipeline and persists the full lifecycle transactionally in SQLite.

## Design decisions retained

- Keep checker contracts provider-neutral and identity/version explicit.
- Treat VS-114 accessibility evidence as immutable upstream authority.
- Normalize findings before decision logic so ordering and evidence remain reproducible.
- Require bounded, evidenced and approved waivers for blocking findings.
- Use optimistic concurrency, exact replay receipts, append-only history and deterministic Outbox effects.
- Keep approval fail-closed under authority drift, digest mismatch, stale revision or transaction failure.

## Lessons

- A preflight result is only useful as downstream authority when checker inputs, outputs and versions are part of the evidence identity.
- Persisting the complete state snapshot plus normalized relational records supports both deterministic replay and operational inspection.
- Same-SHA CI evidence must be the final gate because documentation or test additions invalidate earlier workflow evidence.

## Follow-forward

The next dependency-ready slice must consume only an approved, current VS-115 evidence digest and must not reinterpret or mutate technical-preflight history.

# VS-112 GREEN evidence

## Dual TDD GREEN

- GREEN-I: provider-neutral print PDF contracts, deterministic orchestrator, durable SQLite schema and store are implemented.
- GREEN-E: governance contract proves exact authority, deterministic geometry/pagination, font and image gates, replay, optimistic concurrency, restart recovery, append-only history and deterministic Outbox.

## Verified behavior

- Exact current approved VS-111 authority is required before rendering, validation or decision.
- Geometry, page boxes, page ordering, recto/verso identity, metadata and artifact digests are deterministic.
- Font embedding permissions, glyph coverage, image rights, color profiles, effective DPI and accessibility alternatives fail closed.
- Render submission materializes the artifact before atomic persistence.
- Validation records typed findings and blocks approval when blocking findings remain.
- Exact replay is idempotent and conflicting reuse fails closed.
- SQLite is the restart-safe authority; history and receipts reconstruct the current state.
- All writes, findings, decisions, receipts, history and Outbox effects are committed in one transaction.

Final same-head CI evidence must be recorded in the PR before merge.

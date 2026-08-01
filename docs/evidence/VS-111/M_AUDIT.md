# VS-111 Auditoría M

## Scope

Independent adversarial review of the VS-111 specification, contracts, authority checks, deterministic package builder, SQLite schema, durable store, governance test and Dual TDD evidence.

## Findings reviewed

- Authority: only the exact current approved VS-110 manuscript revision may render.
- Determinism: section, node, resource and package-entry ordering are explicit and stable.
- EPUB structure: `mimetype` is first and stored; container, navigation and OPF paths are canonical.
- Accessibility: figures without governed alternatives fail closed.
- Rights and integrity: resources require approval, safe paths and matching content digests.
- Durable materialization: the generated package and entries are persisted before the render can be validated or approved.
- Replay and concurrency: persisted payload digests reject conflicting reuse and SQL revision predicates reject stale writers.
- Atomicity: render state, entries, receipts, history and Outbox effects share one transaction.

## Adversarial cases

Stale authority, cross-workspace authority, duplicate resource paths, unsafe paths, digest mismatch, missing figure alternative, duplicate entry order, invalid `mimetype`, stale revision, conflicting replay, blocking EPUBCheck-compatible findings and transaction failure must not expose an approved or partial render.

## Result

PASS, conditional on Plan Integrity, Governance Gates and .NET CI all succeeding on the exact final SHA and complete GREEN_EVIDENCE, META_AUDIT and RETROSPEC remaining present.

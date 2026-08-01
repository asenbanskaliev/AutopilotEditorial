# VS-111 GREEN Evidence

## Dual TDD result

RED-I and RED-E were recorded before implementation. GREEN is provided by provider-neutral EPUB contracts, exact VS-110 authority validation, deterministic package construction, durable SQLite persistence, replay-safe receipts, optimistic concurrency, append-only history, deterministic Outbox effects and the VS-111 governance contract.

## Verified behavior

- One exact approved VS-110 manuscript authority is required.
- XHTML, navigation and package documents use deterministic ordering and stable paths.
- The `mimetype` entry is first and stored without compression.
- Figures require governed accessibility alternatives.
- Resources are path-safe, rights-approved and content-digest verified.
- The materialized package is passed to the durable store before submission.
- Package metadata and every entry are persisted atomically in SQLite.
- Exact replay, stale revision, cross-workspace access and conflicting request reuse fail closed.
- Restart reconstruction uses durable history and receipts rather than process memory.
- Blocking validation findings prevent approval.

## Prior implementation validation

Implementation head `fd5b843ad5fe27db6811b0ca5f87267243c756b0` passed Plan Integrity #1287, Governance Gates #1187 and .NET CI #1084 before the materialization remediation.

The exact final evidence head must independently pass all three required workflows before merge eligibility.

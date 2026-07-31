# VS-110 RetroSpec

## What changed from RED to GREEN

The repository moved from having no canonical manuscript source to a durable, authority-bound assembly lifecycle with typed contracts, deterministic manifests and digests, explicit total ordering, SQLite persistence, replay receipts, optimistic concurrency, append-only history and deterministic Outbox effects.

## Specification confirmations

- Every required approved source is included exactly once or explicitly excluded when optional.
- Front matter, body and back matter use stable section and node ordering.
- Editorial, research, rights, provenance, visual and accessibility lineage remains exact.
- Missing, duplicate, stale, cross-boundary or digest-mismatched authority fails closed.
- Figures require accessibility alternatives.
- Approval freezes an immutable canonical manuscript revision for downstream renderers.

## Corrections discovered during implementation

Contracts and orchestration were insufficient without durable persistence and governance proof. SQLite reconstruction, persisted replay, revision guards, append-only history, deterministic Outbox and a VS-110 governance contract were added before final acceptance.

## Residual risks and controls

Canonical assembly quality still depends on correctness of upstream approved sources. The workflow contains this risk through exact authority verification, deterministic manifests, explicit findings, fail-closed approval and immutable approved revisions.

## Final acceptance rule

No earlier workflow result may be reused after the final evidence commit. The exact final SHA must independently pass Plan Integrity, Governance Gates and .NET CI before ready-for-review or merge.

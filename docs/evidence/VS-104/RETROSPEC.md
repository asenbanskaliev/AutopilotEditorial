# VS-104 RetroSpec

## What changed from RED to GREEN

The repository moved from having no governed cover workflow to a durable system with typed contracts, deterministic print/ebook/thumbnail variants, exact upstream authority, fail-closed geometry and placement validation, SQLite persistence, replay receipts, optimistic concurrency, append-only history and deterministic Outbox events.

## Specification confirmations

- Cover variants remain bound to exact current VS-100, VS-101 and VS-103 authority.
- Print geometry includes trim, bleed, spine and barcode safe-area constraints.
- Ebook and thumbnail outputs retain independent format and crop requirements.
- Typography, contrast, placement, lineage and rights evidence are required before approval.
- Invalid, incomplete or stale evidence cannot advance the workflow.
- Selection, repair, rejection, approval and supersession are explicit governed transitions.
- Durable history and receipts support restart-safe reads and exact replay.

## Corrections discovered during implementation

The initial contracts and orchestration established fail-closed cover decisions but lacked a concrete durable store. SQLite persistence and a governance contract were added before acceptance. A governance assertion was then corrected to validate the implemented `LineageEvidenceDigest` behavior instead of requiring an unrelated `Path` token.

## Residual risks and controls

Printer-specific production tolerances and provider-rendered typography can vary. The common workflow contains this through exact geometry, evidence digests, independent variant validation, explicit repair transitions and blocked approval on missing or failed checks. Concrete printer and rendering-provider integration belongs with downstream slices.

## Final acceptance rule

No earlier workflow result may be reused after this final evidence commit. The exact final SHA must independently pass Plan Integrity, Governance Gates and .NET CI before ready-for-review or merge.

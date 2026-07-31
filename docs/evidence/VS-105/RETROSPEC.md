# VS-105 RetroSpec

## What changed from RED to GREEN

The repository moved from having no governed visual-accessibility workflow to a durable, authority-bound lifecycle with typed contracts, deterministic orchestration, SQLite persistence, replay receipts, optimistic concurrency, append-only history and deterministic Outbox events.

## Specification confirmations

- Meaningful visuals require textual alternatives.
- Decorative status is explicit and governed.
- Complex visuals can require long descriptions.
- Essential text embedded in images requires equivalent text.
- Contrast, reading order and caption association are evidence-bearing.
- Approval depends on current VS-101, VS-103 and VS-104 authority as applicable.
- Failed, stale or conflicting operations fail closed and cannot inherit approval.

## Corrections discovered during implementation

The initial contracts and orchestration were insufficient without a durable store and governance proof. SQLite persistence, restart reconstruction, replay-safe receipts, optimistic revision checks and the VS-105 governance contract were added before final acceptance.

## Residual risks and controls

Accessibility quality still depends on evidence quality and human judgment for nuanced descriptions. The workflow contains that risk through explicit findings, deterministic required checks, fail-closed approval, durable evidence and governed decisions.

## Final acceptance rule

No earlier workflow result may be reused after the final evidence commit. The exact final SHA must independently pass Plan Integrity, Governance Gates and .NET CI before ready-for-review or merge.

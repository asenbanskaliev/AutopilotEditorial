# VS-102 RetroSpec

## What changed from RED to GREEN

The repository moved from having no provider-neutral image-adapter boundary to a durable workflow with typed contracts, normalized provider execution, bounded retry/cancellation behavior, exact visual-brief authority, authoritative asset registration, SQLite persistence, replay receipts, optimistic concurrency, append-only history and deterministic Outbox events.

## Specification confirmations

- Provider-specific response shapes remain outside the application contract.
- Capability negotiation is explicit and versioned.
- Manual ingestion follows the same path, digest, provenance, rights, accessibility and technical rules as generated output.
- A request cannot become completed until every accepted output is linked to a VS-101 asset registration.
- Failed, cancelled, stale or conflicting operations fail closed and cannot inherit a successful state.
- Durable history snapshots and receipts support restart-safe reads and exact replay.

## Corrections discovered during implementation

The initial implementation covered orchestration but lacked a concrete durable `IImageAdapterRequestStore`. The slice was therefore not accepted despite green CI. A SQLite-backed store and a governance contract were added so persistence, replay, restart recovery, revision guarding and atomic Outbox behavior are explicit and testable.

## Residual risks and controls

External providers can still exhibit non-determinism, partial availability and inconsistent usage reporting. These risks are contained behind normalized attempts, bounded retry policy, provider evidence digests, immutable output digests and fail-closed registration. Provider-specific end-to-end tests belong with each concrete adapter implementation while this slice governs the common contract.

## Final acceptance rule

No earlier workflow result may be reused after the final evidence commit. The exact final SHA must independently pass Plan Integrity, Governance Gates and .NET CI before ready-for-review or merge.

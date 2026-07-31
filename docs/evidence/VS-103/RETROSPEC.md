# VS-103 RetroSpec

## What changed from RED to GREEN

The repository moved from having no provider-neutral visual-audit workflow to a durable policy-governed system with typed contracts, normalized technical and semantic checks, exact visual-brief and asset authority, adapter-provenance linkage, fail-closed aggregation, SQLite persistence, replay receipts, optimistic concurrency, append-only history and deterministic Outbox events.

## Specification confirmations

- Provider-native semantic payloads remain outside the application contract.
- Every required policy check must have exactly one normalized, evidenced result.
- Unknown, skipped, partial or missing checks cannot count as PASS.
- Human review is triggered by policy or insufficient semantic confidence.
- Waivers are scoped and expiring and cannot suppress stale authority, cross-boundary, rights, provenance or digest failures.
- Completion requires exact current VS-100 and VS-101 authority.
- Durable history and receipts support restart-safe reads and exact replay.

## Corrections discovered during implementation

The initial orchestration established policy coverage and fail-closed aggregation but lacked a concrete durable store. SQLite persistence and a governance contract were added before acceptance so authority checks, replay, revision guarding, decision/waiver history and Outbox behavior are explicit and testable.

## Residual risks and controls

Semantic providers may be non-deterministic or return confidence that is poorly calibrated. The common contract contains this through minimum-confidence policy, deterministic evidence digests, complete coverage checks, human escalation and blocking treatment for unevidenced or partial results. Provider-specific end-to-end calibration belongs with concrete provider slices.

## Final acceptance rule

No earlier workflow result may be reused after this final evidence commit. The exact final SHA must independently pass Plan Integrity, Governance Gates and .NET CI before ready-for-review or merge.

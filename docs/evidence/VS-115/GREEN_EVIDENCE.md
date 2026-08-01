# VS-115 Dual TDD GREEN evidence

## GREEN-I — implementation-facing

- Provider-neutral technical-preflight request, authority, checker, finding, waiver, decision and state contracts are implemented.
- The orchestrator requires the exact current approved VS-114 authority and executes checkers in deterministic identity/version order.
- Rule profiles, checker input/output digests, normalized findings and the immutable technical-preflight evidence digest are explicit and reproducible.
- Blocking findings, stale authority, invalid waivers and stale revisions fail closed.
- SQLite is the durable authority for runs, checker executions, findings, waivers, decisions, replay receipts, append-only history and deterministic Outbox messages.
- Mutations are transactional, optimistic-concurrency guarded, workspace isolated, replay safe and restart reconstructable.

## GREEN-E — external/governance-facing

The cumulative governance contract proves the required source files, exact upstream authority, deterministic checker orchestration, complete durable schema, replay reconstruction, revision guard, atomic transaction and Outbox integration. Final PASS remains conditional on Plan Integrity, Governance Gates and .NET CI succeeding on the same unchanged final SHA.

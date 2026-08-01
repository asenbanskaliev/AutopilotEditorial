# VS-116 Dual TDD RED evidence

## RED-I — implementation-facing

Expected contracts and behavior are intentionally absent at slice start:

- no typed KDP package request, exact VS-115 authority, metadata profile, validation finding, manifest, package receipt, decision or state contracts;
- no deterministic metadata normalization, canonical manifest construction or reproducible ZIP assembly;
- no fail-closed approval over missing metadata, rights, identifier or AI-disclosure requirements;
- no replay-safe, concurrency-safe durable SQLite store, append-only history or Outbox for KDP packages.

## RED-E — external/governance-facing

The cumulative journey cannot yet prove that:

1. one exact approved VS-115 authority produces deterministic KDP metadata and package evidence;
2. manuscript and cover files are bound by verified digests and safe normalized paths;
3. marketplace/profile requirements, rights, identifiers and AI-content disclosure are validated without invented values;
4. stale, missing, digest-mismatched, cross-workspace or non-approved inputs fail closed;
5. unresolved blocking metadata findings prevent approval;
6. canonical manifest and ZIP construction reproduce the same digest for identical governed inputs;
7. restart and exact replay preserve one authoritative package and one deterministic Outbox effect;
8. transaction or packaging failure leaves no partial approved state.

No PASS claim is permitted until implementation, cumulative tests, GREEN_EVIDENCE, Auditoría M, Meta-Audit, RetroSpec, Plan Integrity, Governance Gates and .NET CI are complete and green on one final SHA.

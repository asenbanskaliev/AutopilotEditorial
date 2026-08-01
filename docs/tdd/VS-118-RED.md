# VS-118 Dual TDD RED evidence

## RED-I — implementation-facing

Expected contracts and behavior are intentionally absent at slice start:

- no typed professional release request, exact VS-117 authority, frozen artifact, canonical manifest, decision or state contracts;
- no deterministic artifact verification, canonical manifest assembly or immutable release evidence digest construction;
- no fail-closed freeze, approval or governed supersession workflow;
- no replay-safe, concurrency-safe durable SQLite store, append-only history or Outbox for professional releases.

## RED-E — external/governance-facing

The cumulative journey cannot yet prove that:

1. one exact approved VS-117 proof authority produces one immutable professional release;
2. every required manuscript, cover, metadata, accessibility, preflight and proof artifact is verified by digest and length;
3. release manifest ordering, identities and hashes are canonical and reproducible;
4. stale, missing, digest-mismatched, cross-workspace, rejected or superseded inputs fail closed;
5. incomplete inventories or absent approvals prevent freeze and approval;
6. approved releases are immutable and changes require governed supersession;
7. restart and exact replay preserve one authoritative release result and one deterministic Outbox effect;
8. transaction failure leaves no partial frozen or approved release state;
9. internal release approval does not fabricate external marketplace publication.

No PASS claim is permitted until implementation, cumulative tests, GREEN_EVIDENCE, Auditoría M, Meta-Audit, RetroSpec, Plan Integrity, Governance Gates and .NET CI are complete and green on one final SHA.

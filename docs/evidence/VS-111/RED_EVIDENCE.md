# VS-111 Dual TDD RED evidence

## RED-I — implementation-facing

Expected contracts and behavior are intentionally absent at slice start:

- no typed EPUB render request, exact manuscript authority, package, entry, validation, decision or state contracts;
- no deterministic XHTML, navigation, OPF or ZIP assembly;
- no EPUBCheck-compatible validation evidence or accessibility enforcement;
- no replay-safe, concurrency-safe durable SQLite store, append-only history or Outbox for EPUB rendering.

The implementation journey must turn these missing capabilities GREEN without weakening VS-110 or earlier guarantees.

## RED-E — external/governance-facing

The cumulative journey currently cannot prove that:

1. one exact approved VS-110 manuscript revision renders to deterministic EPUB bytes;
2. ordered sections and semantic nodes become valid XHTML, navigation, manifest and spine entries;
3. metadata, citations, notes, figures, captions and accessibility alternatives retain exact authority;
4. stale, missing, digest-mismatched, cross-workspace or non-approved inputs fail closed;
5. package entry paths, timestamps, ordering, media types and compression are reproducible;
6. EPUBCheck-compatible blocking findings prevent approval;
7. restart and exact replay preserve one authoritative output and one deterministic Outbox effect;
8. transaction failure leaves no partial approved render.

No PASS claim is permitted until implementation, cumulative tests, GREEN_EVIDENCE, Auditoría M, Meta-Audit, RetroSpec, Plan Integrity, Governance Gates and .NET CI are complete and green on one final SHA.

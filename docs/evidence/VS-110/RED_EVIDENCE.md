# VS-110 Dual TDD RED evidence

## RED-I — implementation-facing

Expected contracts and behavior are intentionally absent at slice start:

- no typed canonical manuscript assembly, ordered section, content-node, source-binding, validation, decision or state contracts;
- no exact authority validation across approved authoring, editorial, research, rights, visual and accessibility outputs;
- no deterministic manifest, canonical digest or total ordering for front matter, body and back matter;
- no replay-safe, concurrency-safe durable SQLite store, append-only history or Outbox for manuscript assembly.

The implementation journey must turn these missing capabilities GREEN without weakening earlier slice guarantees.

## RED-E — external/governance-facing

The cumulative journey currently cannot prove that:

1. one immutable canonical manuscript source includes every required approved input exactly once;
2. section and content ordering are explicit and deterministic;
3. citations, rights, provenance, figures, captions and accessibility alternatives retain exact authority;
4. stale, duplicate, missing, digest-mismatched or cross-workspace inputs fail closed;
5. renderers receive a frozen canonical revision and cannot mutate it;
6. restart and exact replay preserve one authoritative result and one deterministic Outbox effect;
7. transaction failure leaves no partial assembly.

No PASS claim is permitted until implementation, cumulative tests, GREEN_EVIDENCE, Auditoría M, Meta-Audit, RetroSpec, Plan Integrity, Governance Gates and .NET CI are complete and green on one final SHA.

# VS-119 Dual TDD RED evidence

## RED-I — implementation-facing

Expected contracts and behavior are intentionally absent at slice start:

- no immutable language authority separating UI locale from book locale;
- no canonical BCP-47 normalization or locale-profile resolution;
- no provider-neutral compiled language contract bound to AI invocations;
- no deterministic post-generation language and regional-variant validation;
- no governed multilingual exception, bounded retry or fail-closed acceptance flow;
- no replay-safe, concurrency-safe durable SQLite store, append-only history or Outbox for language governance.

## RED-E — external/governance-facing

The cumulative journey cannot yet prove that:

1. a project configured as `es-ES` produces Spanish content and rejects unintended English drift;
2. a project configured as `en-US` produces English content and rejects unintended Spanish drift;
3. `es-ES`, `es-MX`, `en-US` and `en-GB` regional conventions remain distinct and reproducible;
4. UI language can differ from book language without changing generated content;
5. every planning, generation, rewriting, copyediting and metadata invocation binds the exact language-policy digest;
6. quotations, names, citations and approved multilingual passages are allowed only inside explicit bounded scopes;
7. stale, missing, digest-mismatched or cross-workspace policies fail closed;
8. restart and exact replay preserve one authoritative validation and one deterministic Outbox effect;
9. transaction failure leaves no partially accepted language state.

No PASS claim is permitted until implementation, cumulative tests, GREEN_EVIDENCE, Auditoría M, Meta-Audit, RetroSpec, Plan Integrity, Governance Gates and .NET CI are complete and green on one final SHA.

# VS-119 GREEN_EVIDENCE

Status: PASS pending same-head repository CI confirmation.

## Dual TDD GREEN-I

- Immutable provider-neutral language authority and invocation contracts exist.
- Book language is separated from UI language.
- BCP-47 tags are canonicalized and initial locale profiles cover es-ES, es-MX, en-US and en-GB.
- Every compiled invocation includes a deterministic language contract, policy digest and instruction digest.
- Generated text is evaluated after generation; unintended language drift and regional-variant violations fail closed.
- Quotations, proper nouns, citations and multilingual passages require bounded approved scopes.
- SQLite persistence provides atomic state, findings, decisions, replay receipts, append-only history and deterministic Outbox effects.

## Dual TDD GREEN-E

The cumulative governance test proves Spanish/English policy separation, provider neutrality, fail-closed validation, regional variants, durable replay, optimistic concurrency and required audit artifacts.

No merge claim is made by this document alone. Final PASS requires Plan Integrity, Governance Gates and .NET CI green on the exact final SHA.

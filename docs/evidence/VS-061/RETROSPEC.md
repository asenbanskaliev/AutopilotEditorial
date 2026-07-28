# VS-061 RetroSpec

## What changed

The repository now supports durable local paragraph-coherence audits tied to one exact approved scene digest.

## Learned constraints

- Negative tests must isolate one invariant; reusing an existing finding identity masked the intended range validation.
- Paragraph offsets are part of the durable contract and must be computed deterministically from the immutable approved text.
- Blocking findings must remain explicit until a governed terminal decision exists.
- Closure must be atomic with its Outbox event and idempotency receipt.

## Follow-through

Future coherence slices should reuse exact causal binding, stable source ranges, append-only findings, terminal decisions and evidence-driven close gates rather than introducing disconnected analyzers.

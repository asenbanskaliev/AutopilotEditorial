# VS-054 RED evidence

## Baseline

Before this slice, the repository had no durable BookPlan aggregate, no causal validation from an approved specification, no structural/DAG validation, no plan lifecycle, no exactly-once approval event and no restart journey.

## Independent RED expectations

- creation from missing or non-approved specification fails closed;
- duplicate part/chapter keys fail;
- invalid or non-contiguous ordering fails;
- missing dependency targets fail;
- cyclic chapter dependencies fail;
- stale version/revision fails;
- request ID replay with changed fingerprint fails;
- prepare/commit/approve out of order fails;
- approval does not duplicate Outbox messages;
- approved versions cannot be overwritten.

Status: RED captured. GREEN requires an executable cumulative journey and all CI gates.

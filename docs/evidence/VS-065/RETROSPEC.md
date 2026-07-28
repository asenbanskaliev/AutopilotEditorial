# VS-065 RetroSpec

## What changed

The repository now maintains durable character and object state, inventory ownership and attributable transfer history as a projection of active narrative facts.

## Learned constraints

- Snapshot creation and every transfer require an exact active `FACT` authority.
- Transfer authority must match project, transition, entity, dimension and temporal validity.
- A caller-provided fingerprint is not sufficient replay evidence; the canonical command payload must also be hashed and compared.
- Object transfer updates the current authority references and appends immutable transfer history atomically.
- Terminal lifecycle operations are required when the public status model declares `SUPERSEDED` and `RETRACTED`.
- State, receipt and Outbox must commit or roll back together.

## Follow-through

`VS-066 Timeline plot threads` must consume these materialized states and transfer histories as authoritative temporal inputs. It must not infer holder, location or character condition from prose when durable state exists.
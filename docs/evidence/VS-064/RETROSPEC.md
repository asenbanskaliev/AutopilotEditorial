# VS-064 RetroSpec

## What changed

The repository now maintains durable narrative knowledge state tied to an exact closed transition.

## Learned constraints

- Knowledge authority must reference the exact transition close message.
- Facts, beliefs and secrets require different disclosure behavior.
- Knowers and excluded actors must remain disjoint.
- Contradictory active statements for the same subject/object fail closed.
- Activation receipt and Outbox event commit atomically.

## Follow-through

Character, object and timeline slices must consume this durable knowledge state instead of reconstructing it from text.

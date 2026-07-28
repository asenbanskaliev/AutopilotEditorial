# VS-065 — Character object state

## Intent

Persistir el estado narrativo de personajes y objetos como proyección durable de conocimiento activo y transiciones cerradas, sin reconstruir inventario, localización o condiciones desde texto libre.

## Behaviors

1. Toda mutación requiere autoridad causal exacta desde `VS-064 Knowledge state` activo y su transición cerrada.
2. El estado de personaje se expresa mediante dimensiones versionadas: ubicación, condición física, condición emocional, relación, objetivo, capacidad y posesión.
3. El estado de objeto conserva identidad, tipo, condición, ubicación, poseedor y disponibilidad.
4. Una transferencia de objeto requiere poseedor/origen actual exacto y no puede duplicar ni perder inventario.
5. Estados y transferencias conservan vigencia temporal y procedencia atribuible.
6. Cambios incompatibles con hechos activos fallan cerrados; creencias no pueden mutar estado objetivo por sí solas.
7. El ciclo es `DRAFT → ACTIVE → SUPERSEDED|RETRACTED` para snapshots y append-only para transferencias.
8. Replay exacto es idempotente; reutilización conflictiva de identidad o request ID falla cerrada.
9. Concurrencia optimista, aislamiento por workspace, rollback atómico y recuperación tras reinicio son obligatorios.
10. Activación de estado y transferencia emiten Outbox exactly-once.

## Invariants

- Un objeto no puede tener simultáneamente dos poseedores activos.
- Una transferencia no puede crear ni destruir una instancia de inventario.
- El origen de una transferencia debe coincidir con el estado activo inmediatamente anterior.
- Las dimensiones activas de un mismo personaje no pueden solaparse con valores objetivos incompatibles en la misma vigencia.
- Solo conocimiento `FACT` activo puede autorizar estado objetivo; `BELIEF` y `SECRET` regulan perspectiva y visibilidad, no verdad material.

## Gates

- Autoridad exacta desde conocimiento activo y transición cerrada.
- Inventario conservativo y transferencias causales.
- Replay, concurrencia, rollback, reinicio y workspace isolation.
- Outbox exactly-once para activación y transferencia.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec y CI completo.

# VS-066 — Timeline plot threads

## Intent

Persistir cronología narrativa y tramas como proyecciones durables de conocimiento y estado materializado, evitando reconstrucciones desde texto libre.

## Behaviors

1. Todo evento temporal requiere autoridad exacta desde conocimiento activo y, cuando aplique, estado de personaje/objeto vigente.
2. Los eventos conservan tiempo narrativo, duración, participantes, localización, procedencia y causalidad.
3. Las dependencias causales forman un DAG; ciclos y referencias futuras imposibles fallan cerrados.
4. Las tramas conservan identidad, objetivo, estado, hitos, dependencias y resolución.
5. Un avance de trama requiere un evento temporal activo que satisfaga el hito esperado.
6. Contradicciones con hechos, localización, poseedor, condición o vigencia materializada fallan cerradas.
7. El lifecycle de evento es `DRAFT → ACTIVE → SUPERSEDED|RETRACTED`; el de trama es `OPEN → ACTIVE → RESOLVED|ABANDONED`.
8. Replay exacto es idempotente; reutilización conflictiva de identidad o request ID falla cerrada mediante hash canónico del payload.
9. Concurrencia optimista, aislamiento por workspace, rollback atómico y recuperación tras reinicio son obligatorios.
10. Activación de evento y avance/cierre de trama emiten Outbox exactly-once.

## Invariants

- Ningún participante u objeto puede ocupar estados incompatibles en el mismo intervalo.
- Un objeto no puede aparecer con poseedor o localización distintos de su estado materializado vigente.
- Un evento no puede depender directa o indirectamente de sí mismo.
- Una trama no puede resolverse con hitos obligatorios abiertos.
- Solo fuentes activas y temporalmente válidas pueden autorizar cronología objetiva.

## Gates

- Autoridad exacta y coherencia temporal.
- DAG causal y tramas gobernadas.
- Replay, concurrencia, rollback, reinicio y workspace isolation.
- Outbox exactly-once.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec y CI completo.
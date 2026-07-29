# VS-068 — Chapter gate lock

## Intent

Cerrar un capítulo mediante una auditoría acumulativa reproducible, una decisión explícita y un lock durable que impida mutaciones posteriores no autorizadas.

## Behaviors

1. El gate consume el capítulo, sus escenas, transition audits, knowledge state, character/object state, timeline/plot threads y repair patches aplicados.
2. La evaluación declara workspace, proyecto, capítulo, versión, digest, rule set, actor y evidencia causal exacta.
3. Los findings se clasifican por severidad, regla, alcance y evidencia; cualquier finding bloqueante abierto impide aprobación y lock.
4. La decisión puede ser `APPROVE`, `REJECT` o `RETURN_TO_REPAIR` y siempre conserva actor, razón, timestamp y revisión esperada.
5. La aprobación válida genera un lock con digest y versión exactos del capítulo y de sus proyecciones dependientes.
6. Un capítulo bloqueado rechaza modificaciones de contenido, estado, timeline o conocimiento que afecten su snapshot.
7. Reabrir exige una operación explícita, autoridad atribuible y motivo; el lock anterior permanece en historial append-only.
8. Replay exacto es idempotente; reutilización conflictiva de identidad o request ID falla cerrada comparando payload real.
9. Concurrencia optimista, aislamiento por workspace, rollback atómico y recuperación tras reinicio son obligatorios.
10. Decisión, lock y reapertura emiten Outbox exactly-once.

## Invariants

- No existe lock válido con findings bloqueantes abiertos.
- El digest bloqueado debe coincidir con el contenido y proyecciones evaluados.
- Un lock no se sobrescribe; cada reapertura y nuevo lock genera historial.
- Ninguna mutación parcial puede dejar capítulo y proyecciones en estados de lock distintos.
- Un replay no duplica decisiones, locks, historial ni eventos.

## Gates

- Auditoría acumulativa reproducible.
- Decisión atribuible y lock exacto por digest/version.
- Protección efectiva frente a mutaciones posteriores.
- Replay, concurrencia, rollback, reinicio y workspace isolation.
- Outbox exactly-once.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec y CI completo.
# VS-069 — Memory commit

## Intent

Consolidar en una operación transaccional un `MemoryDelta` derivado de un capítulo bloqueado, manteniendo causalidad, procedencia y consistencia entre todas las proyecciones narrativas.

## Behaviors

1. El delta declara workspace, proyecto, capítulo, gate lock, versión y digest exactos, actor y evidencia causal.
2. Solo un gate de capítulo `LOCKED` y no reabierto puede autorizar la preparación y commit.
3. El delta contiene cambios tipados para conocimiento, estados de personajes/objetos, timeline y plot threads.
4. La preparación calcula un payload hash canónico y valida que cada cambio corresponde al snapshot bloqueado.
5. El commit aplica todas las entradas y actualiza sus proyecciones en una única transacción; cualquier error provoca rollback completo.
6. El lifecycle es `PROPOSED → VALIDATED → COMMITTED|REJECTED|STALE`.
7. Drift en lock, versión, digest o proyección convierte el delta en `STALE` sin mutaciones parciales.
8. El historial es append-only y conserva snapshot anterior, cambios aplicados, actor, timestamps y autoridad.
9. Replay exacto es idempotente; reutilización conflictiva de delta o request ID falla cerrada comparando payload real.
10. Concurrencia optimista, aislamiento por workspace, recuperación tras reinicio y Outbox exactly-once son obligatorios.

## Invariants

- No existe commit sin un lock de capítulo vigente y exacto.
- Un delta no puede mezclar workspaces, proyectos, capítulos o gates.
- Todas las entradas se aplican o ninguna se aplica.
- El estado previo permanece reconstruible desde el historial.
- Replay no duplica commits, historial ni eventos.

## Gates

- Autoridad exacta desde `VS-068 Chapter gate lock`.
- Delta canónico, tipado y validado contra snapshot.
- Atomicidad, rollback y detección de drift.
- Replay, concurrencia, restart y workspace isolation.
- Outbox exactly-once.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec y CI completo.
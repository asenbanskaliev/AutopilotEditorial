# VS-070 — Cross chapter audit

## Intent

Auditar de forma global y reproducible la coherencia narrativa entre capítulos bloqueados y memorias ya comprometidas, detectando contradicciones, omisiones y regresiones antes de iniciar las pasadas editoriales profesionales.

## Behaviors

1. La auditoría declara workspace, proyecto, rango de capítulos, locks, memory commits, rule set, actor y evidencia causal exacta.
2. Solo consume capítulos `LOCKED` y `MemoryDelta` comprometidos y vigentes.
3. Evalúa continuidad de conocimiento, estados de personajes/objetos, inventario, timeline y plot threads entre capítulos.
4. Los findings se clasifican por severidad, regla, capítulos afectados, alcance y evidencia reproducible.
5. Cualquier finding bloqueante abierto impide aprobar la auditoría global.
6. La decisión puede ser `APPROVE`, `REJECT` o `RETURN_TO_REPAIR` y conserva actor, razón, timestamp y revisión esperada.
7. El snapshot auditado queda fijado por versiones y digests exactos; drift posterior marca la auditoría `STALE`.
8. El historial es append-only y permite reconstruir evaluaciones, decisiones y reaperturas.
9. Replay exacto es idempotente; reutilización conflictiva de identidad o request ID falla cerrada comparando payload real.
10. Concurrencia optimista, rollback atómico, aislamiento por workspace, recuperación tras reinicio y Outbox exactly-once son obligatorios.

## Invariants

- No existe aprobación global con findings bloqueantes abiertos.
- Una auditoría no puede mezclar workspaces o proyectos.
- Todos los locks y memory commits deben coincidir con el snapshot evaluado.
- Una transición fallida no deja findings, decisiones ni eventos parciales.
- Replay no duplica auditorías, historial ni eventos.

## Gates

- Cobertura global de continuidad intercapítulo.
- Findings reproducibles y decisión atribuible.
- Snapshot exacto y detección de drift.
- Replay, concurrencia, rollback, reinicio y workspace isolation.
- Outbox exactly-once.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec y CI completo.

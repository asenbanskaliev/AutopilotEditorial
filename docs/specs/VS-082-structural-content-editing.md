# VS-082 — Structural content editing

## Intent

Ejecutar una pasada reproducible de edición estructural y de contenido sobre una revisión developmental aprobada y vigente, verificando orden, profundidad, continuidad, cobertura, redundancias y huecos antes de liberar la siguiente pasada profesional.

## Behaviors

1. La revisión declara workspace, proyecto, plan editorial, revisión developmental, nodo structural/content, snapshot, rule set, actor y evidencia causal exacta.
2. Solo una revisión `VS-081` aprobada, vigente y asociada al nodo `structural/content` dependency-ready puede autorizar la revisión.
3. La evaluación cubre orden de capítulos y escenas, profundidad de tratamiento, continuidad de contenido, cobertura de objetivos, redundancias, huecos y material fuera de alcance.
4. Cada finding conserva severidad, regla, localización, capítulos o escenas afectados y evidencia reproducible.
5. La decisión puede ser `APPROVE`, `REJECT` o `RETURN_TO_REPAIR` y conserva actor, razón, timestamp y revisión esperada.
6. Ninguna revisión con findings bloqueantes abiertos puede aprobarse ni liberar el gate del nodo.
7. El snapshot queda fijado por versiones y digests exactos; drift posterior marca la revisión `STALE`.
8. El historial es append-only y permite reconstruir evaluación, decisiones, reparación y reapertura.
9. Replay exacto es idempotente; reutilización conflictiva de identidad o request ID falla cerrada comparando payload real.
10. Concurrencia optimista, rollback atómico, aislamiento por workspace, recuperación tras reinicio y Outbox exactly-once son obligatorios.

## Invariants

- No existe revisión sin autoridad exacta desde `VS-081`.
- Una revisión no puede mezclar workspaces, proyectos, planes, revisiones developmental o snapshots.
- No existe aprobación con findings bloqueantes abiertos.
- Una transición fallida no deja findings, decisiones, gates ni eventos parciales.
- Replay no duplica revisiones, historial, gate records ni eventos.

## Gates

- Autoridad exacta desde la revisión developmental aprobada.
- Cobertura reproducible de orden, profundidad, continuidad, redundancias y huecos.
- Decisión atribuible y gate verde solo tras aprobación válida.
- Detección de drift, replay, concurrencia, rollback, reinicio y workspace isolation.
- Outbox exactly-once.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec y CI completo.

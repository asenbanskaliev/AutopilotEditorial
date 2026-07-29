# VS-081 — Developmental editing

## Intent

Ejecutar una pasada de edición de desarrollo reproducible sobre un plan editorial profesional bloqueado, verificando promesa, estructura, alcance, arcos, progresión y huecos antes de permitir la siguiente pasada.

## Behaviors

1. La revisión declara workspace, proyecto, plan editorial, nodo developmental, snapshot, rule set, actor y evidencia causal exacta.
2. Solo un plan vigente de `VS-080` con el nodo `developmental` dependency-ready puede autorizar la revisión.
3. La evaluación cubre promesa editorial, estructura global, alcance, arcos, progresión, redundancias macro y huecos de contenido.
4. Cada finding conserva severidad, regla, localización, capítulos afectados y evidencia reproducible.
5. La decisión puede ser `APPROVE`, `REJECT` o `RETURN_TO_REPAIR` y conserva actor, razón, timestamp y revisión esperada.
6. Ninguna revisión con findings bloqueantes abiertos puede aprobarse ni liberar el gate del nodo.
7. El snapshot queda fijado por versiones y digests exactos; drift posterior marca la revisión `STALE`.
8. El historial es append-only y permite reconstruir evaluación, decisiones, reparación y reapertura.
9. Replay exacto es idempotente; reutilización conflictiva de identidad o request ID falla cerrada comparando payload real.
10. Concurrencia optimista, rollback atómico, aislamiento por workspace, recuperación tras reinicio y Outbox exactly-once son obligatorios.

## Invariants

- No existe revisión sin autoridad exacta desde `VS-080`.
- Una revisión no puede mezclar workspaces, proyectos, planes o snapshots.
- No existe aprobación con findings bloqueantes abiertos.
- Una transición fallida no deja findings, decisiones, gates ni eventos parciales.
- Replay no duplica revisiones, historial, gate records ni eventos.

## Gates

- Autoridad exacta desde el plan editorial bloqueado.
- Cobertura reproducible de promesa, estructura, alcance, arcos y huecos.
- Decisión atribuible y gate verde solo tras aprobación válida.
- Detección de drift, replay, concurrencia, rollback, reinicio y workspace isolation.
- Outbox exactly-once.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec y CI completo.

# VS-083 — Voice line editing

## Intent

Ejecutar una pasada reproducible de edición de voz y línea sobre una revisión structural/content aprobada y vigente, verificando voz, claridad, ritmo, precisión, consistencia estilística y legibilidad antes de permitir la siguiente pasada profesional.

## Behaviors

1. La revisión declara workspace, proyecto, plan editorial, revisión structural/content, snapshot, rule set, actor y evidencia causal exacta.
2. Solo una revisión `VS-082` aprobada, vigente y asociada al nodo `voice/line` dependency-ready puede autorizar la revisión.
3. La evaluación cubre voz narrativa, claridad de frase, ritmo, precisión léxica, consistencia estilística, legibilidad y densidad.
4. Cada finding conserva severidad, regla, localización, capítulos, escenas, párrafos o spans afectados y evidencia reproducible.
5. La decisión puede ser `APPROVE`, `REJECT` o `RETURN_TO_REPAIR` y conserva actor, razón, timestamp y revisión esperada.
6. Ninguna revisión con findings bloqueantes abiertos puede aprobarse ni liberar el gate del nodo.
7. El snapshot queda fijado por versiones y digests exactos; drift posterior marca la revisión `STALE`.
8. El historial es append-only y permite reconstruir evaluación, decisiones, reparación y reapertura.
9. Replay exacto es idempotente; reutilización conflictiva de identidad o request ID falla cerrada comparando payload real.
10. Concurrencia optimista, rollback atómico, aislamiento por workspace, recuperación tras reinicio y Outbox exactly-once son obligatorios.

## Invariants

- No existe revisión sin autoridad exacta desde `VS-082`.
- Una revisión no puede mezclar workspaces, proyectos, planes, revisiones structural/content o snapshots.
- No existe aprobación con findings bloqueantes abiertos.
- Una transición fallida no deja findings, decisiones, gates ni eventos parciales.
- Replay no duplica revisiones, historial, gate records ni eventos.

## Gates

- Autoridad exacta desde la revisión structural/content aprobada.
- Cobertura reproducible de voz, claridad, ritmo, precisión, consistencia y legibilidad.
- Decisión atribuible y gate verde solo tras aprobación válida.
- Detección de drift, replay, concurrencia, rollback, reinicio y workspace isolation.
- Outbox exactly-once.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec y CI completo.

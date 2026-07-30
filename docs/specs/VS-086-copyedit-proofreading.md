# VS-086 — Copyedit and proofreading

## Intent

Ejecutar una pasada reproducible de copyedit y proofreading sobre una revisión themes/pacing aprobada y vigente, corrigiendo lenguaje y presentación sin alterar de forma silenciosa la intención narrativa.

## Behaviors

1. La revisión declara workspace, proyecto, plan editorial, revisión themes/pacing, snapshot, rule set, actor y autoridad causal exacta.
2. Solo una revisión `VS-085` aprobada, vigente y asociada al nodo `copyedit/proofreading` dependency-ready puede autorizarla.
3. La evaluación cubre gramática, ortografía, puntuación, sintaxis, terminología, estilo editorial, formato y tipografía.
4. Cada finding conserva severidad, regla, localización, capítulos, escenas, párrafos, líneas o spans y evidencia reproducible.
5. La decisión puede ser `APPROVE`, `REJECT` o `RETURN_TO_REPAIR` y conserva actor, razón, timestamp y revisión esperada.
6. Ninguna revisión con findings bloqueantes abiertos puede aprobarse.
7. El snapshot queda fijado por versiones y digests exactos; drift posterior marca la revisión `STALE`.
8. El historial es append-only y reconstruye evaluación, decisiones, reparación y reapertura.
9. Replay exacto es idempotente; reutilización conflictiva de identidad o request ID falla cerrada comparando payload real.
10. Concurrencia optimista, rollback atómico, aislamiento por workspace, recuperación tras reinicio y Outbox exactly-once son obligatorios.

## Invariants

- No existe revisión sin autoridad exacta desde `VS-085`.
- Una corrección no puede mezclar workspaces, proyectos, planes, revisiones fuente o snapshots.
- No existe aprobación con findings bloqueantes abiertos.
- Una transición fallida no deja findings, decisiones, gates ni eventos parciales.
- Replay no duplica revisiones, historial ni eventos.

## Gates

- Autoridad exacta desde la revisión themes/pacing aprobada.
- Cobertura reproducible de gramática, ortografía, puntuación, sintaxis, terminología, estilo, formato y tipografía.
- Decisión atribuible y gate verde solo tras aprobación válida.
- Drift, replay, concurrencia, rollback, reinicio y workspace isolation.
- Outbox exactly-once.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec y CI completo.

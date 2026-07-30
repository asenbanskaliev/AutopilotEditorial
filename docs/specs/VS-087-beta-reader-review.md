# VS-087 — Beta reader review

## Intent

Ejecutar una pasada reproducible de beta readers sobre una revisión copyedit/proofreading aprobada y vigente, consolidando feedback diverso, trazable y accionable antes de permitir la siguiente pasada profesional.

## Behaviors

1. La revisión declara workspace, proyecto, plan editorial, revisión copyedit/proofreading, snapshot, panel de lectores, rule set, actor y evidencia causal exacta.
2. Solo una revisión `VS-086` aprobada, vigente y asociada al nodo `BETA_READERS` dependency-ready puede autorizar la revisión.
3. El panel conserva perfiles de lector, cobertura objetivo, anonimización opcional, conflictos de interés y estado de participación.
4. Cada respuesta conserva lector, dimensión, severidad, localización, capítulos, escenas, párrafos o spans afectados, evidencia y recomendación.
5. La consolidación identifica consenso, discrepancias, outliers, cobertura insuficiente y findings editoriales reproducibles.
6. La decisión puede ser `APPROVE`, `REJECT` o `RETURN_TO_REPAIR` y conserva actor, razón, timestamp y revisión esperada.
7. Ninguna revisión con findings bloqueantes abiertos, cobertura mínima incumplida o conflictos críticos puede aprobarse.
8. El snapshot queda fijado por versiones y digests exactos; drift posterior marca la revisión `STALE`.
9. El historial es append-only y permite reconstruir panel, respuestas, consolidación, decisiones, reparación y reapertura.
10. Replay exacto es idempotente; reutilización conflictiva de identidad o request ID falla cerrada comparando payload real.
11. Concurrencia optimista, rollback atómico, aislamiento por workspace, recuperación tras reinicio y Outbox exactly-once son obligatorios.

## Invariants

- No existe revisión sin autoridad exacta desde `VS-086`.
- Una revisión no puede mezclar workspaces, proyectos, planes, revisiones copyedit/proofreading o snapshots.
- No existe aprobación con findings bloqueantes abiertos ni cobertura insuficiente.
- Una transición fallida no deja respuestas, findings, decisiones, gates ni eventos parciales.
- Replay no duplica revisiones, respuestas, historial, gate records ni eventos.

## Gates

- Autoridad exacta desde la revisión copyedit/proofreading aprobada.
- Cobertura reproducible del panel y feedback localizado.
- Consolidación determinista de consenso, discrepancias y findings.
- Decisión atribuible y gate verde solo tras aprobación válida.
- Detección de drift, replay, concurrencia, rollback, reinicio y workspace isolation.
- Outbox exactly-once.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec y CI completo.

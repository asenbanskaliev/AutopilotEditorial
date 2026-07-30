# VS-090 — Research planning

## Intent

Planificar de forma durable, reproducible y auditable la investigación necesaria para verificar el manuscrito después de una revisión de originalidad y lectura en voz alta aprobada y vigente.

## Behaviors

1. El plan declara workspace, proyecto, revisión de autoridad, digest, versión, actor y evidencia.
2. Solo una revisión `VS-088` aprobada, exacta y no stale puede autorizar la planificación.
3. Las preguntas de investigación son tipadas, priorizadas, localizadas y vinculadas a claims o decisiones editoriales.
4. Cada pregunta define estrategia de fuentes, criterios de calidad, actualidad, cobertura y evidencia esperada.
5. Dependencias, responsables, estado, intentos y gates son explícitos.
6. Evidencia incompleta, autoridad stale o gate fallido bloquean el avance sin mutaciones parciales.
7. Replay exacto es idempotente; reutilización conflictiva de plan o request ID falla cerrada comparando payload real.
8. Historial append-only, concurrencia optimista, rollback, reinicio y aislamiento por workspace son obligatorios.
9. Crear, actualizar, aprobar, bloquear y marcar stale emite Outbox exactly-once.

## Invariants

- No existe plan válido sin autoridad exacta desde `VS-088`.
- Ninguna pregunta aprobada carece de prioridad, alcance, criterio de fuente y evidencia esperada.
- Una transición fallida no deja plan, preguntas, historial ni eventos parciales.
- Replay no duplica planes, preguntas, historial ni eventos.

## Gates

- Autoridad exacta desde `VS-088`.
- Preguntas y estrategias de fuente verificables.
- Bloqueo fail-closed ante drift o evidencia incompleta.
- Replay, concurrencia, rollback, reinicio y workspace isolation.
- Outbox exactly-once.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec y CI completo.

# VS-080 — Editorial pass orchestration

## Intent

Orquestar de forma durable, reproducible y auditable las pasadas editoriales profesionales sobre una auditoría intercapítulo aprobada y vigente.

## Behaviors

1. El plan declara workspace, proyecto, auditoría global, revisión/digest de autoridad, versión, actor y evidencia.
2. Solo una `CrossChapterAudit` `APPROVED` y no stale/reabierta puede autorizar el plan.
3. Las pasadas se modelan como nodos ordenados con dependencias, responsable, estado, intentos, evidencia y gate.
4. El orden canónico inicial es developmental, structural/content, voice/line, dialogue, themes/pacing, copyedit/proofreading, beta readers y originality/read-aloud.
5. Una pasada solo puede comenzar cuando todas sus dependencias están completas y sus gates previos están verdes.
6. Findings bloqueantes, evidencia incompleta o gate fallido bloquean el avance sin mutaciones parciales.
7. Drift de la auditoría base convierte el plan en `STALE` de forma fail-closed.
8. Replay exacto es idempotente; reutilización conflictiva de plan o request ID falla cerrada comparando payload real.
9. Historial append-only, concurrencia optimista, rollback, reinicio y aislamiento por workspace son obligatorios.
10. Planificación, inicio, bloqueo, gate, finalización y stale emiten Outbox exactly-once.

## Invariants

- No existe plan válido sin auditoría global aprobada y vigente.
- Ninguna pasada puede saltarse dependencias o gates.
- Una pasada completada conserva evidencia y resultado inmutables en historial.
- Una transición fallida no deja plan, nodos, historial ni eventos parciales.
- Replay no duplica planes, intentos, historial ni eventos.

## Gates

- Autoridad exacta desde `VS-070`.
- DAG/orden canónico y dependencias verificables.
- Gates por pasada y bloqueo fail-closed.
- Drift, replay, concurrencia, rollback, reinicio y workspace isolation.
- Outbox exactly-once.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec y CI completo.

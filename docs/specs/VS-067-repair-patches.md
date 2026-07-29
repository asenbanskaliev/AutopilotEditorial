# VS-067 — Repair patches

## Intent

Aplicar correcciones narrativas localizadas, mínimas y auditables sobre artefactos y proyecciones autoritativas sin reescrituras amplias ni pérdida de procedencia.

## Behaviors

1. Todo patch declara workspace, proyecto, artefacto objetivo, versión/digest esperados, alcance, operaciones, razón, evidencia y actor.
2. La autoridad causal debe enlazar una finding abierta o una auditoría cerrada que justifique exactamente la reparación.
3. El patch falla cerrado si el artefacto o cualquiera de sus precondiciones ha cambiado desde la propuesta.
4. Las operaciones permitidas son localizadas y tipadas; no se aceptan sustituciones globales opacas.
5. La aplicación debe preservar o actualizar atómicamente conocimiento, estado de personaje/objeto y timeline/plot threads afectados.
6. Antes de aplicar se ejecutan validaciones de coherencia; una reparación no puede introducir nuevos hallazgos bloqueantes.
7. El ciclo es `PROPOSED → VALIDATED → APPLIED|REJECTED|STALE`.
8. Replay exacto es idempotente; reutilización conflictiva de identidad o request ID falla cerrada comparando el payload real.
9. Concurrencia optimista, rollback, reinicio e aislamiento por workspace son obligatorios.
10. Aplicación, rechazo o stale emiten Outbox exactly-once con atribución completa.

## Invariants

- Un patch nunca se aplica sobre un digest distinto al validado.
- El alcance material modificado no puede exceder el alcance declarado.
- La versión previa permanece recuperable y el historial es append-only.
- Ninguna proyección dependiente puede quedar parcialmente actualizada.
- Un patch rechazado o stale no modifica el artefacto ni sus proyecciones.

## Gates

- Autoridad exacta y alcance mínimo verificable.
- Precondiciones por digest/version y detección de drift.
- Validación de coherencia previa y posterior.
- Rollback, replay, reinicio, workspace isolation y Outbox exactly-once.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec y CI completo.

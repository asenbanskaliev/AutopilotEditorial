# VS-091 — Claim verification

## Intent

Verificar de forma durable, reproducible y auditable los claims identificados por un plan de investigación `VS-090` aprobado, exacto y vigente.

## Behaviors

1. La verificación declara workspace, proyecto, plan de investigación, claim, snapshot, versión, actor y evidencia causal exacta.
2. Solo un plan `VS-090` aprobado, vigente y no stale puede autorizar la verificación.
3. Cada claim es tipado, localizado y vinculado a preguntas de investigación y decisiones editoriales.
4. La evidencia conserva fuente, fecha de consulta, vigencia, cobertura, calidad, extracto o referencia reproducible y nivel de confianza.
5. La decisión puede ser `VERIFIED`, `REFUTED`, `INCONCLUSIVE` o `RETURN_TO_RESEARCH`, con actor, razón, timestamp y revisión esperada.
6. Ningún claim con evidencia incompleta, contradictoria no resuelta o fuente inválida puede marcarse `VERIFIED`.
7. El snapshot queda fijado por versiones y digests exactos; drift posterior marca la verificación `STALE`.
8. El historial es append-only y permite reconstruir evaluación, decisiones, reapertura y stale.
9. Replay exacto es idempotente; reutilización conflictiva de identidad o request ID falla cerrada comparando payload real.
10. Concurrencia optimista, rollback atómico, aislamiento por workspace, recuperación tras reinicio y Outbox exactly-once son obligatorios.

## Invariants

- No existe verificación sin autoridad exacta desde `VS-090`.
- Una verificación no puede mezclar workspaces, proyectos, planes, claims o snapshots.
- No existe estado `VERIFIED` con evidencia incompleta o bloqueantes abiertos.
- Una transición fallida no deja claims, evidencias, decisiones ni eventos parciales.
- Replay no duplica verificaciones, historial ni eventos.

## Gates

- Autoridad exacta desde el plan de investigación aprobado.
- Claims y evidencias tipados, localizados y reproducibles.
- Decisión atribuible y bloqueo fail-closed ante evidencia insuficiente o drift.
- Replay, concurrencia, rollback, reinicio y workspace isolation.
- Outbox exactly-once.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec y CI completo.

# VS-092 — Citations bibliography

## Intent

Construir una capa durable, reproducible y auditable de citas y bibliografía a partir de claims `VS-091` verificados, exactos y vigentes.

## Behaviors

1. El registro declara workspace, proyecto, verificación de autoridad, digest, versión, actor y evidencia causal.
2. Solo verificaciones `VS-091` aprobadas, exactas y no stale pueden autorizar citas o entradas bibliográficas.
3. Cada cita es tipada, localizada y vinculada a claim, fuente, edición, página, sección, URL, DOI, ISBN u otro identificador aplicable.
4. La bibliografía canónica deduplica fuentes sin perder variantes, procedencia ni historial.
5. El estilo de citación, locale y reglas de renderizado quedan versionados y reproducibles.
6. Cobertura incompleta, metadatos inválidos, enlaces rotos, fuente stale o conflicto de autoridad bloquean la aprobación sin mutaciones parciales.
7. Replay exacto es idempotente; reutilización conflictiva de identidad o request ID falla cerrada comparando payload real.
8. Historial append-only, concurrencia optimista, rollback, recuperación tras reinicio y aislamiento por workspace son obligatorios.
9. Crear, actualizar, validar, aprobar, bloquear y marcar stale emite Outbox exactly-once.

## Invariants

- No existe cita válida sin autoridad exacta desde `VS-091`.
- Ninguna cita aprobada carece de localización, claim, fuente y evidencia.
- Una transición fallida no deja citas, bibliografía, historial ni eventos parciales.
- Replay no duplica citas, fuentes, historial ni eventos.

## Gates

- Autoridad exacta desde `VS-091`.
- Cobertura, metadatos, deduplicación y renderizado verificables.
- Bloqueo fail-closed ante drift, evidencia incompleta o fuente inválida.
- Replay, concurrencia, rollback, reinicio y workspace isolation.
- Outbox exactly-once.
- DUAL_GREEN, Auditoría M, Meta-Audit, RetroSpec y CI completo.

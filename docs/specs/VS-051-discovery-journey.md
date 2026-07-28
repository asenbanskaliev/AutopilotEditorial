# VS-051 — Discovery journey

## IntentSpec

Un proyecto editorial debe poder completar una fase de descubrimiento estructurada mediante preguntas, respuestas, decisiones y asuntos pendientes antes de generar una propuesta editorial.

## BehaviorSpec

- Cada sesión pertenece a un proyecto y workspace existentes.
- La sesión tiene identidad estable, versión y estados `OPEN`, `COMPLETED`, `CANCELLED`.
- Las preguntas tienen clave estable, orden, tipo, obligatoriedad y versión de esquema.
- Las respuestas son versionadas, atribuibles y reemplazables mientras la sesión siga abierta.
- Las decisiones registran opción elegida, motivo, actor y evidencia de origen.
- Los asuntos pendientes permanecen explícitos y bloquean el cierre cuando son obligatorios.
- Completar valida todas las preguntas obligatorias y congela el snapshot de descubrimiento.
- Repeticiones idénticas son idempotentes; reutilizaciones conflictivas fallan en cerrado.
- Una sesión completada o cancelada es inmutable.
- El cierre emite exactamente un evento Outbox `editorial.discovery.completed`.
- Reiniciar conserva sesión, respuestas, decisiones, pendientes y entrega Outbox.
- No se realizan mutaciones remotas dentro de la transacción.

## Gates

- `DISCOVERY_SCHEMA_PASS`
- `QUESTION_PASS`
- `ANSWER_VERSION_PASS`
- `DECISION_PASS`
- `OPEN_ITEM_PASS`
- `COMPLETION_GATE_PASS`
- `IDEMPOTENCY_PASS`
- `IMMUTABILITY_PASS`
- `OUTBOX_ONCE_PASS`
- `RESTART_PASS`
- `NO_REMOTE_MUTATION_PASS`
- `DUAL_GREEN`
- `M_AUDIT_PASS`
- `META_AUDIT_PASS`
- `RETROSPEC_PASS`

# VS-092 RetroSpec

## Especificación confirmada por implementación

La slice entrega un agregado durable de citas y bibliografía cuya autoridad procede exclusivamente de una verificación `VS-091` exacta, aprobada y vigente.

## Decisiones consolidadas

- La autoridad se fija por identidad, revisión y digest; cualquier drift invalida el agregado.
- Una cita pertenece a un claim y una fuente, conserva tipo, localización, locator, renderizado y evidencia de comprobación.
- Una entrada bibliográfica usa una clave canónica para deduplicación y conserva metadatos reproducibles.
- `VALIDATED` exige cobertura completa y ausencia de bloqueantes; `APPROVED` solo puede seguir a una validación válida.
- Replay exacto devuelve el resultado previo; reutilización conflictiva de request o identidad falla cerrada.
- Cada transición efectiva añade una revisión de historial y, cuando corresponde, un único evento Outbox.

## Aprendizaje incorporado

El journey inicialmente insertaba una autoridad incompleta respecto al esquema real de `VS-091`. El fallo `.NET CI` #970 reveló que `rule_set` es parte obligatoria del contrato durable. El seed fue corregido y `.NET CI` #971 verificó la integración acumulativa. La regla queda incorporada: los journeys dependientes deben sembrar todos los campos obligatorios de la autoridad upstream, no una aproximación mínima.

## No-regresión

La cobertura acumulativa conserva creación, validación, aprobación, replay, conflicto, restart, historial, workspace isolation y Outbox exactly-once. El cierre solo es válido con los tres workflows obligatorios verdes sobre el head documental final.

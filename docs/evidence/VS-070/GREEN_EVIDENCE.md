# VS-070 GREEN Evidence

## DUAL_GREEN

### Internal TDD

- Contratos Application para creación, evaluación, decisión, reapertura y consulta.
- Persistencia SQLite y lifecycle transaccional.
- Replay exacto, concurrencia optimista, rollback y aislamiento por workspace.

### External journey

`CrossChapterAuditJourney` demuestra:

- autoridad exacta desde chapter locks y memory commits vigentes;
- creación y replay idempotente con conflicto de payload fail-closed;
- evaluación global reproducible;
- decisión atribuible y aprobación sin findings bloqueantes;
- historial append-only exactamente una vez;
- Outbox exactly-once;
- durabilidad tras reinicio y aislamiento por workspace.

## Reparación de CI

El run `.NET CI` #863 falló porque el fixture intentaba duplicar una entidad en `memory_projection_entries`, tabla que representa la proyección actual y cuya clave primaria es `(workspace_id, projection, entity_id)`. El fixture fue corregido para sembrar entidades válidas e independientes; el journey completo pasó en `.NET CI` #864.

## Resultado

DUAL_GREEN: PASS.

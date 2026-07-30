# VS-093 GREEN Evidence

## Scope

Expedientes durables de derechos y licencias autorizados por una bibliografía `VS-092` aprobada, exacta y vigente.

## DUAL_GREEN

- RED-I: no existían contratos Application para representar expedientes de derechos, alcance territorial, idiomas, canales, vigencia, restricciones y decisiones gobernadas.
- GREEN-I: `RightsLicenseContracts` define creación, evaluación, decisión, revocación, expiración, reapertura, stale, replay y lectura.
- RED-E: no existía persistencia durable ni journey acumulativo para autoridad, restricciones, vigencia, replay, concurrencia, reinicio, aislamiento por workspace e integración Outbox.
- GREEN-E: migración `0038_rights_licenses.sql`, `SqliteRightsLicenseStore` y `RightsLicenseJourney` ejercitan los comportamientos requeridos.

## Behaviors verified

- autoridad exacta desde `VS-092` aprobada y no stale;
- activo, titular, fuente, alcance, territorios, idiomas, canales y restricciones tipados;
- vigencia temporal y evidencia reproducible;
- bloqueo fail-closed ante alcance vacío, evidencia insuficiente, autoridad inválida o restricciones incompatibles;
- aprobación, rechazo, revocación, expiración, reapertura y stale atribuibles;
- replay idempotente y conflicto por payload real;
- concurrencia optimista, rollback atómico, reinicio y aislamiento por workspace;
- historial append-only y Outbox exactly-once.

## Verified functional head

`3dccf8e2b15521aa72382c3b18b97ccd86bcada1`

- Plan Integrity #1161: PASS
- Governance Gates #1071: PASS
- `.NET CI` #979: PASS

Resultado funcional: DUAL_GREEN PASS. El cierre documental debe validarse nuevamente sobre su head final antes del merge.

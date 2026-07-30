# VS-092 RED Evidence

## RED-I

El repositorio puede verificar claims, pero todavía no puede gobernar citas y bibliografía canónicas con autoridad exacta, cobertura reproducible, deduplicación, renderizado versionado y gate durable.

## RED-E

Faltan comportamientos ejecutables para:

- validar autoridad exacta desde verificaciones `VS-091` aprobadas, vigentes y dependency-ready;
- registrar citas tipadas y localizadas vinculadas a claims y fuentes;
- persistir bibliografía canónica, variantes y deduplicación trazable;
- validar metadatos, identificadores, vigencia, cobertura y enlaces;
- renderizar estilos versionados de forma reproducible;
- decidir aprobación o bloqueo sin permitir cobertura incompleta;
- detectar drift y marcar `STALE` sin mutaciones parciales;
- replay/conflicto, concurrencia, rollback, restart y workspace isolation;
- Outbox exactly-once para creación, actualización, validación, decisión y stale.

VS-092 permanece RED hasta que contratos, migración, store transaccional y journey acumulativo demuestren estos comportamientos.

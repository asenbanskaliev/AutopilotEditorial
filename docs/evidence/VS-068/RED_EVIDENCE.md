# VS-068 RED Evidence

## RED-I

El repositorio puede auditar transiciones, persistir conocimiento y estado, mantener timeline y aplicar reparaciones, pero no puede cerrar un capítulo mediante una decisión y lock durables.

## RED-E

Faltan comportamientos ejecutables para:

- auditoría acumulativa de un capítulo y sus proyecciones;
- findings bloqueantes y no bloqueantes reproducibles;
- decisión `APPROVE|REJECT|RETURN_TO_REPAIR`;
- lock exacto por versión y digest;
- protección frente a mutaciones posteriores;
- reapertura explícita e historial append-only;
- replay/conflicto, stale revision, rollback, restart y workspace isolation;
- Outbox exactly-once para decisión, lock y reapertura.

VS-068 permanece RED hasta que contratos, migración, store transaccional y journey acumulativo demuestren estos comportamientos.
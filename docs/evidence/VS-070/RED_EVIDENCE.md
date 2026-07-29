# VS-070 RED Evidence

## RED-I

El repositorio puede bloquear capítulos y comprometer `MemoryDelta`, pero todavía no puede demostrar coherencia global entre capítulos mediante una auditoría durable, reproducible y causalmente enlazada.

## RED-E

Faltan comportamientos ejecutables para:

- validar autoridad desde chapter locks y memory commits vigentes;
- evaluar continuidad global de conocimiento, estados, inventario, timeline y tramas;
- producir findings intercapítulo bloqueantes y no bloqueantes reproducibles;
- decidir `APPROVE|REJECT|RETURN_TO_REPAIR`;
- detectar drift y marcar `STALE` sin mutaciones parciales;
- conservar historial append-only;
- replay/conflicto, stale revision, rollback, restart y workspace isolation;
- Outbox exactly-once para evaluación, decisión y stale.

VS-070 permanece RED hasta que contratos, migración, store transaccional y journey acumulativo demuestren estos comportamientos.

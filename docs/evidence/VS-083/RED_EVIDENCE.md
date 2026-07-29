# VS-083 RED Evidence

## RED-I

El repositorio puede ejecutar y aprobar la pasada structural/content, pero todavía no puede ejecutar ni gobernar la pasada voice/line con findings reproducibles, decisión atribuible y gate durable.

## RED-E

Faltan comportamientos ejecutables para:

- validar autoridad exacta desde una revisión `VS-082` aprobada, vigente y el nodo voice/line dependency-ready;
- fijar snapshot, versiones y digests causales;
- evaluar voz, claridad, ritmo, precisión, consistencia estilística, legibilidad y densidad;
- persistir findings tipados, localizados y reproducibles;
- decidir `APPROVE`, `REJECT` o `RETURN_TO_REPAIR` sin permitir aprobación con bloqueantes;
- registrar gate, detectar drift y marcar `STALE` sin mutaciones parciales;
- replay/conflicto, concurrencia, rollback, restart y workspace isolation;
- Outbox exactly-once para evaluación, decisión, gate, reparación y stale.

VS-083 permanece RED hasta que contratos, migración, store transaccional y journey acumulativo demuestren estos comportamientos.

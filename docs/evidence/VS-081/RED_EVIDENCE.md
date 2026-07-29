# VS-081 RED Evidence

## RED-I

El repositorio puede orquestar el plan de pasadas editoriales, pero todavía no puede ejecutar ni gobernar la pasada developmental con findings reproducibles, decisión atribuible y gate durable.

## RED-E

Faltan comportamientos ejecutables para:

- validar autoridad exacta desde el plan `VS-080` y el nodo developmental dependency-ready;
- fijar snapshot, versiones y digests causales;
- evaluar promesa, estructura, alcance, arcos, progresión y huecos;
- persistir findings tipados y reproducibles;
- decidir `APPROVE`, `REJECT` o `RETURN_TO_REPAIR` sin permitir aprobación con bloqueantes;
- registrar gate, detectar drift y marcar `STALE` sin mutaciones parciales;
- replay/conflicto, stale revision, rollback, restart y workspace isolation;
- Outbox exactly-once para evaluación, decisión, gate, reparación y stale.

VS-081 permanece RED hasta que contratos, migración, store transaccional y journey acumulativo demuestren estos comportamientos.

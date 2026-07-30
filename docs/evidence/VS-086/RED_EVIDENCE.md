# VS-086 RED Evidence

## RED-I

El repositorio puede aprobar la pasada themes/pacing, pero todavía no puede ejecutar ni gobernar copyedit/proofreading con autoridad causal exacta, findings reproducibles y decisión durable.

## RED-E

Faltan comportamientos ejecutables para:

- validar una revisión `VS-085` aprobada, vigente y el nodo copyedit/proofreading dependency-ready;
- fijar snapshot, versiones y digests causales;
- evaluar gramática, ortografía, puntuación, sintaxis, terminología, estilo editorial, formato y tipografía;
- persistir findings tipados y localizados;
- decidir `APPROVE`, `REJECT` o `RETURN_TO_REPAIR` sin permitir aprobación con bloqueantes;
- detectar drift y marcar `STALE` sin mutaciones parciales;
- replay/conflicto, concurrencia, rollback, restart y workspace isolation;
- Outbox exactly-once.

VS-086 permanece RED hasta que contratos, migración, store transaccional y journey acumulativo demuestren estos comportamientos.

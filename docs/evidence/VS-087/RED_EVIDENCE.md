# VS-087 RED Evidence

## RED-I

El repositorio puede ejecutar y aprobar la pasada copyedit/proofreading, pero todavía no puede ejecutar ni gobernar una revisión beta-reader durable, reproducible y dependency-ready.

## RED-E

Faltan comportamientos ejecutables para:

- validar autoridad exacta desde una revisión `VS-086` aprobada, vigente y el nodo beta-readers dependency-ready;
- fijar snapshot, panel, perfiles, versiones y digests causales;
- registrar participación, cobertura y conflictos de interés;
- persistir feedback tipado, localizado y reproducible;
- consolidar consenso, discrepancias, outliers y cobertura insuficiente;
- decidir `APPROVE`, `REJECT` o `RETURN_TO_REPAIR` sin permitir aprobación con bloqueantes o cobertura insuficiente;
- registrar gate, detectar drift y marcar `STALE` sin mutaciones parciales;
- replay/conflicto, concurrencia, rollback, restart y workspace isolation;
- Outbox exactly-once para panel, respuestas, consolidación, decisión, reparación y stale.

VS-087 permanece RED hasta que contratos, migración, store transaccional y journey acumulativo demuestren estos comportamientos.

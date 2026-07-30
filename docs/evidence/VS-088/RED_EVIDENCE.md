# VS-088 RED Evidence

## RED-I

El repositorio puede completar la pasada beta-reader, pero todavía no puede ejecutar ni gobernar una revisión de originalidad y lectura en voz alta con autoridad exacta, findings reproducibles, decisión atribuible y gate durable.

## RED-E

Faltan comportamientos ejecutables para:

- validar autoridad exacta desde una revisión `VS-087` aprobada, vigente y el nodo originality/read-aloud dependency-ready;
- fijar snapshot, versiones y digests causales;
- evaluar similitud indebida, clichés, repetición, cadencia, tropiezos, pronunciación, sonoridad y fluidez;
- persistir findings tipados, localizados y reproducibles;
- decidir `APPROVE`, `REJECT` o `RETURN_TO_REPAIR` sin permitir aprobación con bloqueantes;
- registrar gate, detectar drift y marcar `STALE` sin mutaciones parciales;
- replay/conflicto, concurrencia, rollback, restart y workspace isolation;
- Outbox exactly-once para evaluación, decisión, gate, reparación y stale.

VS-088 permanece RED hasta que contratos, migración, store transaccional y journey acumulativo demuestren estos comportamientos.
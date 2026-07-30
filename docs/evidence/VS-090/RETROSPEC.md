# VS-090 RetroSpec

## Implemented contract

VS-090 materializa un plan de investigación durable y auditable autorizado únicamente por una revisión `VS-088` aprobada, exacta y vigente.

## Confirmed semantics

- El plan fija workspace, proyecto, autoridad, digest, versión, actor y evidencia.
- Las preguntas conservan tipo, prioridad, localización, vínculo causal, estrategia de fuentes y evidencia esperada.
- Los estados y dependencias son explícitos y gobernados.
- Aprobar requiere preguntas completas y listas; bloqueo, drift o evidencia incompleta impiden avance.
- Replay exacto no duplica estado; reutilización conflictiva falla cerrada.
- Toda mutación conserva atomicidad, historial append-only y Outbox exactly-once.
- Reinicio, concurrencia e aislamiento por workspace forman parte del contrato operativo.

## Delta respecto a la spec inicial

No hay cambios de intención ni relajación de invariantes. La implementación concretó los modelos de persistencia, receipts, historial y eventos necesarios para demostrar los gates.

## Dependency release

Una aprobación válida de VS-090 puede autorizar VS-091 Claim verification mediante identidad, revisión y digest exactos.
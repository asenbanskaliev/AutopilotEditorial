# VS-085 RetroSpec

## Especificación confirmada tras implementación

La pasada themes/pacing queda modelada como una revisión durable autorizada por una revisión de diálogo aprobada, exacta y vigente, junto con el nodo editorial `THEMESPACING` dependency-ready.

## Comportamiento observado

- Snapshot, rule set, actor y autoridad causal quedan fijados en creación.
- La evaluación reemplaza atómicamente findings tipados y localizados.
- La aprobación queda bloqueada mientras exista cualquier finding bloqueante abierto.
- `RETURN_TO_REPAIR`, reevaluación, aprobación, rechazo, reapertura y stale preservan revisión e historial.
- Drift de autoridad conduce a `STALE` sin escrituras parciales.
- Receipts validan request ID, fingerprint y hash de payload real.
- Historial y eventos Outbox son exactamente una vez dentro de la misma transacción.

## Aprendizaje incorporado

Las siguientes pasadas editoriales deben reutilizar el mismo patrón de autoridad causal exacta, gate dependency-ready, findings reproducibles, lifecycle versionado y evidencia acumulativa.

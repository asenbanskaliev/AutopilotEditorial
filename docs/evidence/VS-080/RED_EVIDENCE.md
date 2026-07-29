# VS-080 RED Evidence

## RED-I

El repositorio puede aprobar una auditoría intercapítulo, pero todavía no puede convertir esa autoridad en un plan durable de pasadas editoriales con dependencias y gates verificables.

## RED-E

Faltan comportamientos ejecutables para:

- validar autoridad desde una `CrossChapterAudit` aprobada y vigente;
- crear y versionar el plan de pasadas;
- modelar orden, dependencias, responsables, intentos y evidencia;
- impedir saltos de dependencia o avance con gates fallidos;
- bloquear por findings o evidencia incompleta;
- detectar drift y marcar `STALE` sin mutaciones parciales;
- replay/conflicto, stale revision, rollback, restart y workspace isolation;
- Outbox exactly-once para planificación, inicio, gate, bloqueo, finalización y stale.

VS-080 permanece RED hasta que contratos, migración, store transaccional y journey acumulativo demuestren estos comportamientos.

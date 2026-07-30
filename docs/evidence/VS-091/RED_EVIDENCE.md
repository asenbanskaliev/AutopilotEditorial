# VS-091 RED Evidence

## RED-I

El repositorio puede planificar la investigación mediante `VS-090`, pero todavía no puede verificar claims con autoridad exacta, evidencia reproducible, decisión atribuible y estado durable.

## RED-E

Faltan comportamientos ejecutables para:

- validar autoridad exacta desde un plan `VS-090` aprobado, vigente y no stale;
- fijar snapshot, versiones y digests causales;
- modelar claims tipados, localizados y vinculados a preguntas de investigación;
- persistir evidencia con fuente, vigencia, cobertura, calidad y confianza;
- decidir `VERIFIED`, `REFUTED`, `INCONCLUSIVE` o `RETURN_TO_RESEARCH` sin permitir verificación inválida;
- detectar drift y marcar `STALE` sin mutaciones parciales;
- replay/conflicto, concurrencia, rollback, restart y workspace isolation;
- Outbox exactly-once para evaluación, decisión, reapertura y stale.

VS-091 permanece RED hasta que contratos, migración, store transaccional y journey acumulativo demuestren estos comportamientos.

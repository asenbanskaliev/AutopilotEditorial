# VS-054 — Book planning

## Intent

Transformar una specification aprobada en un BookPlan durable, versionado, trazable y ejecutable por los siguientes slices de authoring.

## Preconditions

- La specification existe en el mismo workspace y proyecto.
- La versión indicada está `APPROVED` y su `approval_message_id` coincide.
- No existe otro plan activo para la misma specification/version.

## Model

Cada versión contiene partes ordenadas y capítulos ordenados. Cada capítulo declara objetivo, audiencia, entregables, restricciones, criterios de aceptación y dependencias por clave estable.

Estados: `DRAFT → PREPARED → COMMITTED → APPROVED`.

## Invariants

1. Las claves de parte y capítulo son únicas por versión.
2. El orden es positivo, continuo y sin colisiones dentro de su ámbito.
3. Todo capítulo pertenece a una parte existente.
4. Las dependencias apuntan a capítulos existentes de la misma versión.
5. El grafo de dependencias es acíclico.
6. `PREPARED` exige estructura completa y al menos un capítulo.
7. `COMMITTED` fija un digest SHA-256 del contenido canónico.
8. `APPROVED` conserva el digest y emite `editorial.book-plan.approved` exactly-once mediante Outbox.
9. Una versión aprobada es inmutable; los cambios abren una nueva versión append-only.
10. Toda mutación usa request ID idempotente y expected version/revision fail-closed.
11. Aislamiento estricto por workspace.
12. Ningún side effect remoto ocurre dentro de la transacción.

## Gates

- causal authority desde specification aprobada;
- validación estructural y DAG;
- historial append-only;
- idempotencia y conflicto de replay;
- reinicio durable;
- Outbox exactly-once;
- DUAL_GREEN;
- Auditoría M, Meta-Audit y RetroSpec;
- todos los workflows CI verdes.

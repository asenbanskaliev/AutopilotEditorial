# VS-055 — Scene planning

## IntentSpec

Transformar una versión aprobada de BookPlan en un ScenePlan durable, trazable y versionado que descomponga cada capítulo en escenas/secciones ejecutables sin perder orden, objetivos, dependencias ni criterios de aceptación.

## BehaviorSpec

1. Solo un BookPlan aprobado con identidad, versión, approval message y digest coincidentes autoriza crear el ScenePlan.
2. El plan contiene escenas con clave estable, capítulo existente, orden local único, propósito, summary, beats, evidencias requeridas, restricciones, aceptación y dependencias.
3. Todo capítulo del BookPlan debe tener al menos una escena; no se permiten escenas huérfanas.
4. Las dependencias apuntan a escenas existentes, no son autorreferenciales y forman un DAG.
5. Las revisiones son append-only y solo se permiten en `DRAFT`.
6. Ciclo: `DRAFT → PREPARED → COMMITTED → APPROVED`.
7. `COMMIT` fija digest SHA-256; `APPROVE` conserva contenido y emite `editorial.scene-plan.approved` exactly-once por Outbox.
8. Request IDs y fingerprints gobiernan replay idempotente y conflicto fail-closed.
9. Una nueva versión solo se abre desde una versión aprobada y no muta historia.
10. Aislamiento por workspace y recuperación tras reinicio son obligatorios.
11. Ningún efecto remoto ocurre dentro de la transacción.

## Gates

- Autoridad causal BookPlan aprobada.
- Schema y store durable.
- Cobertura completa de capítulos.
- Orden y claves únicas.
- DAG válido.
- Concurrencia optimista e idempotencia.
- Digest e inmutabilidad.
- Outbox exactly-once.
- Restart y aislamiento.
- Journey acumulativo DUAL_GREEN.
- Auditoría M, Meta-Audit y RetroSpec.
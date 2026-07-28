# VS-054 RetroSpec

## Resultado incorporado

Book Planning queda definido como un agregado durable y versionado, autorizado únicamente por una specification aprobada.

## Reglas consolidadas

1. Una combinación `workspace + specification + version` solo puede originar un BookPlan.
2. Las revisiones son append-only; no se sobrescribe historia.
3. Las partes tienen claves y orden global únicos.
4. Los capítulos tienen claves únicas y orden único dentro de cada parte.
5. Cada capítulo pertenece a una parte existente y declara objetivo, audiencia, entregables y aceptación.
6. Las dependencias deben apuntar a capítulos existentes, no pueden ser autorreferenciales y forman un DAG.
7. Solo `DRAFT` admite revisión.
8. `COMMIT` fija el digest; `APPROVE` no altera contenido.
9. Solo una aprobación produce el evento durable `editorial.book-plan.approved`.
10. Una nueva versión se abre desde una versión aprobada sin mutarla.

## Mejora para slices posteriores

Los slices de drafting deben consumir exclusivamente una versión aprobada del BookPlan y conservar `plan_id`, `plan_version`, `approval_message_id` y `content_digest` como evidencia causal.
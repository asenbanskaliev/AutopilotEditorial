# VS-052 RetroSpec

## Aprendizajes incorporados

1. Una discovery completada autoriza una única proposal dentro del workspace; los escenarios alternativos requieren evidencia discovery independiente.
2. La revisión vigente debe permanecer separada de su historial para permitir lectura eficiente sin perder trazabilidad.
3. Rechazo no debe mutar el contenido: obliga a crear una nueva revisión y volver a `DRAFT`.
4. La aprobación es la frontera transaccional que habilita specification lifecycle mediante Outbox exactly-once.

## Endurecimientos para VS-053

- Exigir `proposal_id`, `proposal_revision` y `approval_message_id` como precondiciones causales.
- Mantener versionado optimista y snapshots inmutables.
- No permitir que una specification preparada modifique retroactivamente la proposal aprobada.

Resultado: RETROSPEC_PASS.

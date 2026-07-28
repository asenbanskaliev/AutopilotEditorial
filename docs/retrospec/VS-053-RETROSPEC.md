# VS-053 RetroSpec

## Aprendizajes incorporados

1. La proposal aprobada no basta como referencia nominal: deben fijarse revisión y mensaje de aprobación.
2. Prepare y commit son fronteras distintas; prepare valida completitud y commit congela el digest.
3. Cada cambio de estado se conserva como revisión append-only para auditar cómo se alcanzó la autoridad aprobada.
4. Una nueva versión debe conservar intacta la versión aprobada anterior y retirar temporalmente la autorización vigente.

## Endurecimientos para VS-054

- Fijar `specification_id`, versión, revisión y digest aprobados en la raíz del BookPlan.
- Rechazar partes o capítulos que incumplan constraints y acceptance criteria.
- Mantener orden estable, identidades durables y cambios versionados.

Resultado: RETROSPEC_PASS.

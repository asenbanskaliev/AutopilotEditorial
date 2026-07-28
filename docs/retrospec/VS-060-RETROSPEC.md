# VS-060 RetroSpec

## Qué se aprendió

La generación de escenas debe modelarse como ejecución durable, no como una llamada directa al modelo. El brief causal, la invocación y cada intento son evidencia editorial y operativa.

## Ajustes consolidados

- Los intentos son append-only.
- El texto solo puede avanzar con evidencia para todos los criterios de aceptación.
- Los fallos no reintentables bloquean nuevas ejecuciones.
- El hash del contenido aprobado identifica la salida consumible por los slices de coherencia.
- La aprobación publica un evento exactamente una vez.
- Los journeys nuevos deben ubicarse siempre dentro del proyecto `tests/BookStudio.Tests.Outbox`; el primer CI detectó y corrigió una ubicación errónea sin relajar gates.

## Consecuencia para VS-061

Paragraph coherence debe consumir exclusivamente una escena aprobada y su digest exacto, producir hallazgos locales atribuibles a rangos de párrafo y mantener decisiones/reparaciones separadas del texto fuente.
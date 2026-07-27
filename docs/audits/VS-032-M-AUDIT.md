# VS-032 — Auditoría M

## Resultado

`PASS`

## M1 — Specification

- La reconciliación depende de VS-031, pero mantiene una frontera Application independiente del transporte.
- La superficie remota está limitada a tres GET: `/event`, `/global/event` y `/session/status`.
- Project y global SSE se combinan con polling exclusivamente read-only.
- El parser define UTF-8 estricto, framing SSE y límites aplicados durante la lectura.
- `server.connected` es el handshake obligatorio del stream project.
- Estados conocidos y desconocidos se normalizan sin conservar bodies crudos.
- Deduplicación, backoff, polling, secuencia local, filtrado y task ownership están especificados.
- Tanto cada snapshot como el historial acumulado de estados están acotados.
- No se permite prompt, abort, creación de sesión, selección de modelo/provider, shell, command, file ni otra mutación.

## M2 — Implementation

- `IOpenCodeEventReconciler` expone un `IAsyncEnumerable` provider-neutral.
- `OpenCodeSseParser`:
  - procesa LF y CRLF incrementalmente;
  - admite BOM solo al inicio;
  - une líneas `data`;
  - ignora comentarios y fields desconocidos;
  - valida UTF-8 con fallback estricto;
  - aplica límites de línea, data, fields, type e id antes de acumular sin límite;
  - diferencia EOF de stall.
- `OpenCodeEventNormalizer` reconoce las formas project y global, `server.connected`, `session.status` y eventos desconocidos.
- `OpenCodeSessionStatusParser` es compartido por lifecycle y reconciliación.
- `OpenCodeEventDeduplicator` utiliza namespace por source, event ID o SHA-256 de payload acotado, con expulsión FIFO determinista.
- `OpenCodeBoundedStatusCache`:
  - tiene capacidad exacta `MaximumStatusEntries`;
  - actualiza sesiones existentes sin consumir slots;
  - expulsa FIFO al insertar una sesión nueva en capacidad;
  - mantiene acotado el historial entre snapshots.
- `OpenCodeEventReconciler`:
  - exige health, events.project, events.global y sessions.status;
  - ejecuta como máximo dos pumps SSE y un trigger periódico opcional;
  - usa un channel bounded con backpressure;
  - suprime estados equivalentes conservados en el caché acotado;
  - emite secuencia local estrictamente creciente;
  - reconecta iterativamente con backoff acotado;
  - repara por polling tras connect, EOF, malformed, stall y reconnect;
  - cancela, dispone y espera todos los pumps.

## M3 — Tests

Governance verifica:

- contratos Application provider-neutral;
- parser incremental y límites;
- compatibility gate y GET-only inventory;
- ausencia de métodos y paths de mutación;
- normalizer, dedupe y status parser compartido;
- caché FIFO acumulativo acotado;
- journey real, servidor socket, solución, arquitectura y CI.

El journey contractual real cubre 13 grupos:

1. framing fragmentado, BOM, comentarios, CRLF, multi-line data y EOF incompleto;
2. line, UTF-8, event-data y field-count bounds;
3. project handshake y session.status;
4. global wrapper, directory y retry status;
5. dedupe por ID y fingerprint;
6. expulsión FIFO y reobservación del historial de estados con capacidad 2;
7. EOF reconnect y polling repair busy→idle;
8. malformed handshake reconnect;
9. stall detection y reconnect;
10. reconnect exhaustion;
11. Basic auth y no-leak;
12. session filter;
13. cancellation, early disposal y active-connection cleanup.

Resultado:

```text
OPENCODE_SSE_RECONCILIATION_PASS scenarios=13 requests=57 events=34 gate=NO_MUTATION tasks=NO_LEAKED_TASKS
```

Todos los journeys acumulativos permanecen en PASS.

## M4 — Security and Operations

- Las 57 requests son GET y pertenecen al inventario permitido.
- Basic auth se aplica a health, OpenAPI, streams y polling cuando está configurado.
- El journey no imprime Authorization, endpoint, directory ni event body.
- Los streams no se leen completos ni usan `ReadAsStringAsync`/`ReadToEndAsync`.
- Channel, dedupe, snapshot, historial de estados, line, event, status y backoff están acotados.
- No existe retry handler HTTP; el reconnect es explícito y cancelable.
- El mismo status por SSE/poll no genera duplicados mientras permanece en el caché.
- La expulsión por capacidad permite reobservar posteriormente una sesión sin crecer sin límite.
- Ausencia de una sesión en snapshot no implica idle, delete ni completion.
- Early disposal y caller cancellation dejan `ActiveConnections == 0`.
- No quedan scripts o workflows temporales.

Riesgos residuales:

- Dedupe y status cache son process-local; no ofrecen offsets durables tras restart.
- Una sesión expulsada puede volver a emitirse aunque su estado no haya cambiado; es el coste explícito de memoria acotada.
- El backoff determinista no incluye jitter; un despliegue multi-instancia deberá añadir coordinación/jitter.
- Los unknown provider events se conservan por type, pero no por properties.
- Polling repair garantiza estado actual observable, no la secuencia completa de eventos intermedios perdidos.
- La reconciliación no decide por sí sola que una ejecución editorial haya terminado correctamente.

## M5 — Product Flow

```text
validate watch request
→ verify runtime compatibility
→ start bounded project/global pumps
→ parse strict SSE frames
→ normalize and deduplicate provider event
→ update bounded cross-snapshot status cache
→ emit monotonic provider-neutral event
→ detect connect/EOF/malformed/stall
→ request bounded status snapshot
→ emit only new, changed or previously evicted synthetic status
→ reconnect with bounded delay
→ cancel, dispose and await every owned task
```

## TestChangeRequests

### TCR-032-001

Movió checks estáticos al owner correcto: success marker en `Program.cs`, auth en el journey y captura de headers en el socket genérico.

### TCR-032-002

Corrigió la observación concurrente del escenario EOF esperando tanto el status reparado en el historial como la segunda conexión aceptada, sin exigir duplicados de estado sin cambios.

Ningún comportamiento observable fue eliminado o relajado.

## Audit Remediation 001

La revisión post-merge detectó que el diccionario de estados podía acumular IDs distintos durante toda la watch aunque cada snapshot estuviera acotado.

Corrección:

- `OpenCodeBoundedStatusCache` FIFO;
- capacidad `MaximumStatusEntries`;
- actualización in-place;
- expulsión determinista;
- escenario real `StatusCacheBoundedAsync`.

La evidencia detallada está en `docs/evidence/VS-032/AUDIT_REMEDIATION_001.md` y la RetroSpec complementaria en `docs/retrospec/VS-032-AUDIT-REMEDIATION-001.md`.

## Meta-Audit

- RED original confirmado en head `0693391e54d2ec6857fca82b7e228166ce059c73`.
- El parser, ownership estático y orden de observación EOF se corrigieron mediante los TCR documentados.
- PR #45 fue fusionado después de Plan Integrity, Governance y .NET CI en PASS.
- La revisión posterior identificó el único gap operativo de memoria acumulativa y abrió issue #48 / PR #49.
- La remediación usa el adapter y servidor loopback reales; no mockea `IOpenCodeEventReconciler`.
- El escenario 13 demuestra expulsión y reobservación, no solo presencia de tokens estáticos.
- El workflow permanente conserva `contents: read` y no quedan scripts de migración.
- Application, adapter, tests, solución, arquitectura, catálogo CI y workflow siguen enlazados.

## Evidencia GREEN de la remediación

- Head funcional: `bdfd3f2c8ccf60631341845d8e384e19779f42ea`.
- Plan Integrity: run `30303256533` PASS.
- Governance: run `30303256152` PASS.
- Governance artifact: `8667391996`.
- Governance digest: `sha256:4404b720bf7d5344960aabdbf3cbe43ec148d83dca6cb8e7a666343f53ef16f5`.
- .NET CI: run `30303256913`, job `90101121629` PASS.
- .NET artifact: `8667438911`.
- .NET digest: `sha256:8a992a6a7dedc5e4b3ce0e9c0c30b305fd143d063f14dbfd947db83c9176ab9f`.
- SSE stdout SHA-256: `59e891a1e1ff1886cf618c29fbc179bfcc9f637b9849e6f74e8d8ebf915d09d9`.
- SSE stderr: vacío.

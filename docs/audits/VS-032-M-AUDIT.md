# VS-032 — Auditoría M

## Resultado

`PASS`

## M1 — Specification

- La reconciliación depende de VS-031, pero mantiene una frontera Application independiente del transporte.
- La Spec limita la superficie remota a tres GET:
  - `/event`;
  - `/global/event`;
  - `/session/status`.
- Project y global SSE se combinan con polling exclusivamente read-only.
- El parser define UTF-8 estricto, framing SSE y límites aplicados durante lectura.
- `server.connected` es handshake obligatorio del stream project.
- Estados conocidos y desconocidos se normalizan sin conservar bodies crudos.
- Deduplicación, backoff, polling, secuencia local, filtrado y task ownership están especificados.
- No se permite prompt, abort, sesión, modelo, provider, shell, command, file ni otra mutación.

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
- `OpenCodeEventReconciler`:
  - exige health, events.project, events.global y sessions.status;
  - ejecuta como máximo dos pumps SSE y un trigger periódico opcional;
  - usa un channel bounded con backpressure;
  - conserva un cache acotado de estados y suprime snapshots sin cambio;
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
- journey real, servidor socket, solución, arquitectura y CI.

El journey contractual real cubre 12 grupos:

1. framing fragmentado, BOM, comentarios, CRLF, multi-line data y EOF incompleto;
2. line, UTF-8, event-data y field-count bounds;
3. project handshake y session.status;
4. global wrapper, directory y retry status;
5. dedupe por ID y fingerprint;
6. EOF reconnect y polling repair busy→idle;
7. malformed handshake reconnect;
8. stall detection y reconnect;
9. reconnect exhaustion;
10. Basic auth y no-leak;
11. session filter;
12. cancellation, early disposal y active-connection cleanup.

Resultado:

```text
OPENCODE_SSE_RECONCILIATION_PASS scenarios=12 requests=52 events=27 gate=NO_MUTATION tasks=NO_LEAKED_TASKS
```

Todos los journeys acumulativos permanecen en PASS.

## M4 — Security and Operations

- Las 52 requests son GET y pertenecen al inventario permitido.
- Basic auth se aplica a health, OpenAPI, streams y polling cuando está configurado.
- El journey no imprime Authorization, endpoint, directory ni event body.
- Los streams no se leen completos ni usan `ReadAsStringAsync`/`ReadToEndAsync`.
- Channel, dedupe, snapshot, line, event, status y backoff están acotados.
- No existe retry handler HTTP; el reconnect es explícito y cancelable.
- Misma status por SSE/poll no genera duplicados.
- Ausencia de una sesión en snapshot no implica idle, delete ni completion.
- Early disposal y caller cancellation dejan `ActiveConnections == 0`.
- No quedan scripts o workflows temporales.

Riesgos residuales:

- Dedupe y status cache son process-local; no ofrecen offsets durables tras restart.
- El backoff determinista no incluye jitter; un despliegue multi-instancia deberá añadir coordinación/jitter.
- Los unknown provider events se conservan por type, pero no por properties.
- Polling repair no demuestra pérdida exacta de cada evento intermedio; garantiza estado actual observable.
- La reconciliación no decide por sí sola que una ejecución editorial haya terminado correctamente.

## M5 — Product Flow

```text
validate watch request
→ verify runtime compatibility
→ start bounded project/global pumps
→ parse strict SSE frames
→ normalize and deduplicate provider event
→ update shared session-status cache
→ emit monotonic provider-neutral event
→ detect connect/EOF/malformed/stall
→ request bounded status snapshot
→ emit only new or changed synthetic status
→ reconnect with bounded delay
→ cancel, dispose and await every owned task
```

## TestChangeRequests

### TCR-032-001

Movió checks estáticos al owner correcto:

- success marker en `Program.cs`;
- auth en el journey;
- socket genérico conserva captura de headers.

### TCR-032-002

Corrigió la observación concurrente del escenario EOF:

- el test espera que el historial contenga el status reparado;
- y que la segunda conexión haya sido aceptada;
- sin exigir que el reconciliador duplique un status sin cambios.

Ningún comportamiento observable fue eliminado o relajado.

## Meta-Audit

- RED confirmado en head `0693391e54d2ec6857fca82b7e228166ce059c73`:
  - Plan Integrity run `30290510142` PASS;
  - Governance run `30290510010` FAIL esperado.
- Primer build detectó accesibilidad de options; se hizo pública la configuración bounded requerida por consumidores/tests.
- El primer journey detectó un mínimo arbitrario que impedía límites más estrictos; se permitió cualquier event bound positivo manteniendo máximo acotado.
- Governance detectó únicamente ownership estático; TCR-032-001 lo corrigió sin tocar escenarios.
- El escenario EOF reveló dos condiciones asíncronas observadas en orden incorrecto; TCR-032-002 espera ambas sin solicitar duplicados.
- No quedan workflows, scripts de migración o triggers temporales.
- El journey utiliza sockets loopback y adapter/parser reales; no mockea `IOpenCodeEventReconciler`.
- Application, adapter, tests, solución, arquitectura, catálogo CI y workflow están enlazados.

## Evidencia GREEN funcional

- Head funcional: `03d4131d17659f6f99fd9811d323c8eb1d1d1145`.
- Plan Integrity: run `30298828504` PASS.
- Governance: run `30298828480` PASS.
- Governance artifact: `8665710933`.
- Governance digest: `sha256:b97df5f9d5e6f805082eac94a85cb226b5b50adf6a4a299f5e06e176a0b70112`.
- .NET CI: run `30298828509`, job `90086442096` PASS.
- .NET artifact: `8665759132`.
- .NET digest: `sha256:21a650edf0f6928f8eee50565a0d985c88214ac4c5a12072e48dac0f6a968da3`.
- SSE stdout SHA-256: `bbe71acc6e7696d5446c562abe00058091f3a6f8b57a3b2f65e0bf74c07562f1`.
- SSE stderr: vacío.

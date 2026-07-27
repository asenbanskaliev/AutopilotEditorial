# VS-031 — Auditoría M

## Resultado

`PASS`

## M1 — Specification

- El lifecycle depende explícitamente de VS-030 y separa compatibilidad de ejecución.
- Application posee comandos, resultados, estados, validación, límites y códigos provider-neutral.
- La superficie se limita a create, get, status, prompt_async y abort.
- Prompt acepta únicamente partes de texto acotadas.
- Idempotencia local es obligatoria para create y prompt.
- Abort es explícito y nunca se infiere de timeout, cancelación o estado.
- Los límites de IDs, títulos, partes, requests, responses, status entries y ledger están especificados.
- SSE, modelos, providers, tools, shell, command, files y durable idempotency están excluidos.

## M2 — Implementation

- `IOpenCodeSessionLifecycle` expone cinco casos de uso provider-neutral.
- `OpenCodeSessionValidation` valida Unicode, bytes, controles, IDs seguros y aggregate prompt.
- `OpenCodeSessionLifecycleClient`:
  - reutiliza `OpenCodeEndpointOptions` y el probe de VS-030;
  - cachea solo una compatibilidad válida con las features de sesión requeridas;
  - emite únicamente los cinco endpoints planificados;
  - usa GET/POST exactos y `ResponseHeadersRead`;
  - aplica Basic auth solo cuando está configurado;
  - limita request/response streams;
  - desactiva redirects en el cliente propio;
  - diferencia timeout y cancelación externa;
  - normaliza session/status/abort sin exponer provider DTOs.
- `OpenCodeSessionIdempotencyLedger` usa SHA-256, reserva concurrente, replay estable, conflicto tipado, capacidad acotada y liberación tras fallo/cancelación.
- Create y prompt serializan JSON canónico sin provider/model/agent/tools.
- Session IDs se escapan como un único segmento después de validación restrictiva.
- Status conserva tipos desconocidos como `unknown(providerType)` en vez de tratarlos como idle.

## M3 — Tests

Governance verifica:

- archivos obligatorios;
- neutralidad de Application;
- cinco métodos públicos;
- catálogo de estados y errores;
- compatibility gate y features requeridas;
- métodos/paths exactos;
- ausencia de delete, patch, shell, command, share y file;
- SHA-256, concurrencia, conflicto, capacidad y release del ledger;
- todos los límites;
- journey real, servidor socket, solución, arquitectura y CI.

El journey HTTP real cubre 19 escenarios acumulados:

- compatibility refusal sin mutation;
- create/get y mapping;
- create replay, conflict y colapso concurrente;
- prompt 204, replay y conflict;
- status idle/busy/retry/unknown y orden estable;
- abort true/false;
- Basic auth y no-leak;
- invalid inputs antes de HTTP;
- response bound;
- malformed session/status/abort;
- timeout y caller cancellation;
- failed reservation release y retry.

Resultado:

```text
OPENCODE_SESSION_LIFECYCLE_PASS scenarios=19 requests=50 mutations=15 gate=NO_UNPLANNED_MUTATION
```

Todos los journeys acumulativos permanecen en PASS.

## M4 — Security and Operations

- No hay requests fuera de health/doc y los cinco endpoints de sesión permitidos.
- El journey audita método y path para las 50 requests.
- IDs no pueden introducir slash, backslash, query, fragment o dot segments.
- Prompt text, title, bodies, endpoint y credenciales no aparecen en errores/evidencia.
- Request y response están acotados antes/durante I/O.
- No hay retry automático.
- Failed idempotency reservations se liberan; successes permanecen replayables.
- Concurrent same-key/same-fingerprint emite una sola mutation.
- Same-key/different-fingerprint falla antes de HTTP.
- Basic Authorization se exige en compatibility y mutation durante el escenario auth.
- Unknown provider status se conserva de forma acotada y no se interpreta como completado.

Riesgos residuales:

- El ledger es process-lifetime; persistencia/restart pertenece a Autopilot/outbox.
- El adapter confirma aceptación 204 de prompt_async, no finalización del modelo.
- El polling `/session/status` puede variar entre versiones; VS-032 añadirá reconciliación SSE y no dependerá solo del polling.
- El adapter acepta HTTP 200 para create/get/status/abort y 204 para prompt_async según la baseline actual.
- Provider/model/agent selection permanece fuera de alcance.

## M5 — Product Flow

```text
validate provider-neutral input
→ ensure VS-030 session feature compatibility
→ reserve idempotency when mutating replayable operation
→ serialize bounded canonical body
→ send one exact lifecycle request
→ validate exact status/content type/size
→ parse and normalize provider response
→ complete or release idempotency reservation
→ return provider-neutral result
```

## TestChangeRequest

### TCR-031-001

Movió tres asserts estáticos a su responsabilidad exacta:

- suffixes `"/prompt_async"` y `"/abort"`;
- `Authorization` en el journey, no en el socket genérico;
- error codes tipados de Application en el ledger.

No se eliminó ni rebajó comportamiento observable.

## Meta-Audit

- RED confirmado en head `5472e8f122d073cc9a8f7d82dfebf29279560f9e`:
  - Plan Integrity run `30286539959` PASS;
  - Governance run `30286540726` FAIL esperado.
- La primera implementación compiló y el journey normalizado pasó antes de registrar arquitectura.
- El siguiente head confirmó build, arquitectura y lifecycle PASS.
- Governance detectó únicamente tres asserts estáticos imprecisos; TCR-031-001 los corrigió sin tocar producto/journey.
- No quedan workflows o scripts temporales.
- El journey usa `TcpListener` loopback y el adapter real; no mockea `IOpenCodeSessionLifecycle`.
- Application, adapter, tests, solución, arquitectura, catálogo CI y workflow están enlazados.

## Evidencia GREEN funcional

- Head funcional: `e0c26119ac48d94d98bbbb61bbf4ad3a9cc51b8c`.
- Plan Integrity: run `30288498922` PASS.
- Governance: run `30288498578` PASS.
- Governance artifact: `8661799205`.
- Governance digest: `sha256:b74f55a7fea3e58676a8a537eb5b85d76f1e48ebe2bbb13b0148b93baea75006`.
- .NET CI: run `30288498624`, job `90052129510` PASS.
- .NET artifact: `8661833536`.
- .NET digest: `sha256:919eed4f20795219e06be3e26681298a58ba954eff06f7b96e3e679a772f1a71`.
- Lifecycle stdout SHA-256: `7b185c270768a700acf66ae51c2233640e4e444aa2ced7ea0f51ff9a1c895ebf`.
- Lifecycle stderr: vacío.

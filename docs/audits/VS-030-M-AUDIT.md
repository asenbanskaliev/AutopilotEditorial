# VS-030 — Auditoría M

## Resultado

`PASS`

## M1 — Specification

- La compatibilidad OpenCode separa reachability, health, versión y features.
- El contrato Application es provider-neutral y no referencia HTTP, JSON, URLs, credenciales ni tipos SDK.
- La detección es read-only y no crea sesiones, no envía prompts y no ejecuta modelos.
- La superficie requerida contiene 12 features estables para VS-031 y VS-032.
- El endpoint se valida antes de cualquier petición y solo admite HTTP loopback o HTTPS.
- Las respuestas de health y OpenAPI están acotadas por timeout, bytes y profundidad JSON.
- El resultado usa estados y códigos seguros sin endpoint, body o credenciales.
- El fact `healthy` es triestado: `true`, `false` o `unknown`.

## M2 — Implementation

- `IOpenCodeCompatibilityProbe`, `OpenCodeCompatibilityReport` y `OpenCodeFeatureIds` pertenecen a Application.
- `OpenCodeEndpointOptions` valida scheme, host, user-info, path, query, fragment, credenciales, timeout y límites.
- `OpenCodeCompatibilityProbe`:
  - usa como máximo dos peticiones;
  - solo emite GET;
  - consulta `global/health` y `doc`;
  - usa `ResponseHeadersRead`;
  - lee streams con límite incremental;
  - diferencia timeout de cancelación externa;
  - desactiva redirects en el cliente propio;
  - emite Basic Authorization únicamente cuando está configurado.
- `OpenCodeOpenApiInspector` valida JSON/OpenAPI 3.x, profundidad, paths y operaciones sin invocarlas.
- Los paths parametrizados se normalizan para detectar templates de sesión.
- El catálogo detectado y el conjunto missing se ordenan de forma determinista.
- `ResolveHealthFact` evita inferir health cuando la petición no produjo un payload válido.

## M3 — Tests

Los contratos de Governance verifican:

- archivos de Application, adapter y journey;
- neutralidad provider de Application;
- catálogo exacto de features;
- validación tipada HTTP/HTTPS y loopback;
- GET-only, Basic auth, bounds y cancelación;
- OpenAPI 3.x y operaciones requeridas;
- registro en solución, arquitectura, catálogo CI y workflow.

El journey HTTP contractual real cubre 13 escenarios:

1. servidor compatible;
2. feature requerida ausente;
3. servidor unhealthy;
4. autenticación requerida;
5. Basic auth válida y sin fuga de secretos;
6. health malformado;
7. OpenAPI inválido/no soportado;
8. documentación HTML;
9. health excesivo;
10. specification excesiva;
11. timeout;
12. cancelación externa;
13. validación de endpoint y límites.

También verifica:

- 18 requests totales;
- máximo dos requests por probe;
- únicamente GET;
- únicamente `/global/health` y `/doc`;
- feature matrix de 12 elementos;
- facts `healthy=true|false|unknown` según evidencia real;
- exit code 0 y stderr vacío.

Todos los journeys acumulativos permanecen en PASS.

## M4 — Security and Operations

- HTTP no loopback, FTP, user-info, path, query y fragment son rechazados.
- Username/password están acotados y libres de caracteres de control.
- Password sin username es inválido.
- Credenciales y response bodies no aparecen en reportes, excepciones ni evidencia.
- No hay retries automáticos que oculten fallos.
- Redirects están deshabilitados en el handler propio.
- Content-Length y lectura streaming aplican el mismo límite.
- JSON health usa profundidad 16 y OpenAPI profundidad 64.
- Cancelación del caller se propaga; timeout se mapea a código estable.
- Estados de transporte/auth anteriores a health usan `healthy=unknown`.

Riesgos residuales:

- La detección depende de que OpenCode publique JSON OpenAPI en `/doc`.
- Cambios incompatibles de rutas o nombres requerirán una nueva baseline/version de compatibilidad.
- TLS trust, certificate pinning y process launch no forman parte de esta slice.
- La compatibilidad confirma superficie declarada, no ejecuta operaciones mutadoras.
- SSE se detecta por contrato pero su reconciliación pertenece a VS-032.

## M5 — Product Flow

```text
validate bounded endpoint options
→ GET /global/health
→ validate status/content-type/bytes/JSON
→ derive health and sanitized version
→ stop safely or GET /doc
→ validate status/content-type/bytes/OpenAPI
→ inspect declared operations without invoking them
→ calculate detected and missing features
→ emit compatible/degraded/unhealthy/authentication_required/unavailable
→ preserve tri-state health evidence
```

## TestChangeRequest

### TCR-030-001

Sustituyó asserts débiles de substrings `http`/`https` por tokens tipados `Uri.UriSchemeHttp` y `Uri.UriSchemeHttps`. No cambió comportamiento ni redujo cobertura.

### TCR-030-002

Corrigió la contradicción que marcaba `healthy=true` antes de obtener un health válido. Añadió semántica triestado y asserts acumulativos sin eliminar escenarios.

## Meta-Audit

- RED confirmado en head `03cc2d6e1e1e1158e019322ac0bbf3e6cdfad0b3`:
  - Plan Integrity run `30271221799` PASS;
  - Governance run `30271221654` FAIL esperado por componentes ausentes.
- Los primeros GREEN detectaron dos errores mecánicos de compilación y fueron corregidos sin relajar contratos.
- El journey funcional pasó después con 13 escenarios, 18 requests y 12 features.
- Governance detectó un assert estático débil; `TCR-030-001` lo hizo más preciso.
- Meta-review detectó una contradicción semántica en el fact health; `TCR-030-002` corrigió producto y tests.
- No quedan workflows, scripts ni marcadores temporales.
- No existen mocks de `IOpenCodeCompatibilityProbe`; el journey usa sockets loopback y HTTP real.
- No hay componentes huérfanos: Application, adapter, tests, solución, arquitectura y CI están enlazados.

## Evidencia GREEN funcional

- Head funcional corregido: `8cde1f9ea94bf6ceb6480c978f615816c9b193a0`.
- Plan Integrity: run `30284506174` PASS.
- Governance: run `30284506511` PASS.
- Governance artifact: `8660200965`.
- Governance digest: `sha256:1e349e360e8e840ae03b019cba0b364fd9eb5a545b2bdf788299348975f6dfc2`.
- .NET CI: run `30284506526`, job `90038837852` PASS.
- .NET artifact: `8660239416`.
- .NET digest: `sha256:5b8fc6da96e78e36895e90bdbba9a213f8ff1418db28564aa17a4a6d948adb5f`.
- OpenCode contract result: PASS, exit code 0, stderr vacío.

```text
OPENCODE_COMPATIBILITY_PASS scenarios=13 requests=18 features=12
```

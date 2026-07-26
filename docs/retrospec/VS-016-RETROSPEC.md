# VS-016 — RetroSpec

## Implemented contract

BookStudio dispone de observabilidad OpenTelemetry end-to-end para trazas, métricas y logs, con snapshot local saneado, memoria acotada y exportación OTLP opcional.

## Package contract

Todos los paquetes se gestionan centralmente en versión `1.17.0`:

- `OpenTelemetry`;
- `OpenTelemetry.Extensions.Hosting`;
- `OpenTelemetry.Exporter.OpenTelemetryProtocol`;
- `OpenTelemetry.Instrumentation.AspNetCore`;
- `OpenTelemetry.Instrumentation.Runtime`.

No se permite una versión inline en proyectos.

## Service identity contract

Resource OpenTelemetry:

- service name: `BookStudio.ControlCenter`;
- service version: assembly version;
- deployment environment: environment del host;
- service instance ID: GUID generado por proceso.

No se usan nombre de máquina, usuario, workspace ni identificadores editoriales como atributos de recurso.

## Custom instrumentation contract

`BookStudioTelemetry` ofrece:

- `ActivitySource` `BookStudio`;
- `Meter` `BookStudio`;
- contador de operaciones;
- contador de fallos;
- histograma de duración;
- up/down counter de operaciones activas.

Los nombres de operación deben ser tokens ASCII de hasta 64 caracteres y solo pueden contener letras, números, `.`, `_` o `-`.

## Trace contract

- Se instrumentan rutas ASP.NET conocidas mediante allowlist.
- El endpoint `/api/v1/observability` queda excluido para evitar telemetría recursiva.
- Se respeta propagación W3C `traceparent`.
- Los custom spans se crean mediante `BookStudioTelemetry.StartOperation`.
- Solo se conservan atributos allowlisted y acotados.
- No se capturan query strings, bodies, headers, paths arbitrarios ni exception messages.

## Metric contract

- Se registran los meters de ASP.NET Core, Runtime y BookStudio.
- El snapshot conserva nombre, tipo de instrumento, unidad y timestamp de exportación.
- No conserva dimensiones ni puntos potencialmente de alta cardinalidad.
- `MeterProvider.ForceFlush` debe poder publicar el estado pendiente.

## Log contract

- `Microsoft.Extensions.Logging` se conecta a OpenTelemetry.
- `IncludeFormattedMessage` permanece desactivado.
- Se conserva la plantilla, no el mensaje ya interpolado.
- Los atributos pasan por allowlist y redacción.
- Las excepciones se representan únicamente mediante el nombre de su tipo.
- No se conserva message, stack trace ni inner exception.

## Redaction contract

Se eliminan claves que contengan:

- `password`;
- `secret`;
- `token`;
- `authorization`;
- `cookie`;
- `path`;
- `prompt`;
- `content`;
- `connection`.

Además, los atributos no allowlisted se descartan aunque su nombre no sea sensible. Los valores se limitan a 256 caracteres y las plantillas a 512.

## Snapshot contract

Buffers independientes para traces, metrics y logs:

- capacidad mínima: 16;
- capacidad máxima: 2.048;
- capacidad por defecto: 256;
- orden de lectura: newest-first;
- overflow: descartar el registro más antiguo;
- dropped count acumulado por señal;
- límite de API: 1 a 100 registros por señal.

El snapshot es volátil y pertenece al proceso actual.

## API contract

`GET /api/v1/observability?limit=N` devuelve:

- enabled;
- otlpEnabled;
- capacityPerSignal;
- counts y dropped counts;
- traces, metrics y logs saneados.

Un límite fuera de 1–100 devuelve `400 application/problem+json`.

`GET /api/v1/configuration` añade únicamente:

- `observabilityEnabled`;
- `otlpEnabled`;
- `observabilitySnapshotCapacity`.

Nunca devuelve `OtlpEndpoint`, headers, credenciales ni secretos.

## OTLP contract

- Desactivado por defecto.
- Requiere endpoint explícito cuando se habilita.
- HTTPS permitido.
- HTTP permitido únicamente en loopback.
- Se rechazan credenciales, query strings y fragments.
- El exporter opera fuera de transacciones de aplicación.
- Su indisponibilidad no modifica liveness ni readiness.

## CI contract

Contrato: `dotnet.opentelemetry-integration`.

Journey:

```text
start Kestrel
→ emit custom operations and structured logs
→ send W3C traced request
→ force flush traces and metrics
→ inspect direct snapshot
→ inspect /api/v1/observability
→ verify redaction, bounds and recursive-noise exclusion
→ stop and dispose host
```

## Follow-on constraints

- Las futuras slices deben reutilizar `BookStudioTelemetry` para operaciones de producto en vez de crear meters o sources ad hoc.
- Nuevas dimensiones métricas requieren revisión explícita de cardinalidad.
- Ninguna herramienta MCP podrá registrar prompts, argumentos completos o contenido editorial en logs.
- La retención durable necesitará política propia de privacidad, rotación y borrado.
- Un collector remoto o dashboard no puede introducirse sin health, retry, TLS y configuración de secretos.
- La instrumentación de Worker y otros hosts deberá preservar el mismo service namespace y contratos de redacción.

## Next slice

`VS-020 — MCP initialize`.

# VS-021 — Auditoría M

## Resultado

`PASS`

## M1 — Specification

- El surface activo está limitado a `book.artifact.get` y `book.artifact.compare`.
- `book.project.create`, `book.project.get_status`, `book.project.configure` y `book.decision.submit` quedan reservadas y no se anuncian ni ejecutan.
- Initialize anuncia exactamente `tools` y `resources`; no anuncia prompts, logging, completions, sampling, roots, tasks ni experimental.
- Las dos tools publican `inputSchema`, `outputSchema`, annotations read-only/idempotent y `execution.taskSupport = forbidden`.
- Los resultados estructurados distinguen ejecución completa y error de dominio sin convertir errores esperados en fallos JSON-RPC internos.
- Resources cubre schemas canónicos y versiones inmutables de artefactos mediante URI lógica confinada por proyecto.

## M2 — Implementation

- `BookStudio.Application.Artifacts` contiene el caso de uso provider-neutral para lectura y comparación.
- `BookStudio.Mcp.BookCore` contiene catálogo, schemas, cursores y router MCP; no contiene lógica de filesystem.
- `BookStudio.Infrastructure.Artifacts.FileSystem` continúa siendo el adapter durable.
- `BookCoreRuntime` crea el Artifact Store de forma perezosa; initialize, ping y listados no crean el workspace.
- El dispatcher MCP es asíncrono y el lifecycle JSON-RPC permanece en `McpSession`.
- El catálogo activo es determinista y separado de los nombres reservados.
- Los cursores son versionados, scope-bound, fingerprint-bound y protegidos con checksum comparado en tiempo constante.
- Los paths físicos no forman parte de los modelos Application ni de las respuestas MCP.

## M3 — Tests

Las pruebas estáticas verifican:

- surface activo y reservado exacto;
- schemas de entrada/salida;
- annotations y task support;
- recursos, templates y cursores;
- composición lazy;
- contrato CI `dotnet.book-core-integration`;
- actualización acumulativa del contrato initialize mediante TestChangeRequest.

El journey de subprocess real verifica:

- initialize con capabilities exactas;
- tools/list determinista con dos tools;
- ausencia de tools reservadas;
- resources/list paginado y ordenado;
- rechazo de cursor manipulado;
- resources/templates/list;
- resources/read de schema;
- `book.artifact.get` con texto inline;
- resource link y lectura del texto inmutable;
- `book.artifact.compare` con diff LCS estructurado;
- confinamiento por projectId;
- rechazo de tool reservada;
- artefacto binario sin inline y lectura base64;
- rechazo de resource superior a 1 MiB;
- rechazo de URI desconocida;
- workspace no creado por initialize/list;
- EOF limpio, exit code 0, stdout sin residuos y stderr saneado.

Todos los journeys anteriores continúan en PASS.

## M4 — Security and Operations

- `projectId` y `artifactId` se validan y el artifact debe pertenecer al prefijo lógico del proyecto.
- El contenido inline se limita a texto UTF-8 compatible y 256 KiB.
- La lectura resource se limita a 1 MiB.
- El diff de texto está limitado por tamaño total, número de líneas y máximo de operaciones devueltas.
- Binarios y contenidos grandes no se reinterpretan como texto.
- Las respuestas no incluyen workspace root, `.bookstudio`, manifest paths ni blob paths.
- Los errores de dominio usan códigos estables; no exponen mensajes de excepción.
- El store se abre únicamente al ejecutar una operación que necesita datos.
- No existe egress, shell execution, publicación, mutación de artefactos ni operación destructiva en este surface.

Riesgos residuales:

- El confinamiento actual usa el prefijo `{projectId}.` porque el registro durable de proyectos aún no existe.
- El diff LCS está diseñado para documentos acotados; documentos mayores reciben comparación metadata-only.
- Los cuatro nombres reservados requieren sus slices canónicas antes de poder anunciarse.

## M5 — Product Flow

```text
client launches BookStudio.Mcp
→ initialize advertises tools/resources
→ notifications/initialized
→ tools/list or resources/list
→ tools/call artifact.get / artifact.compare
→ Application query service
→ verified immutable Artifact Store read
→ structuredContent + logical resource link
→ resources/read when requested
→ EOF
→ server exits 0
```

## Meta-Audit

- El primer build falló por precedencia de una switch expression en el padding Base64Url del cursor.
- Se corrigió añadiendo paréntesis a `(padded.Length % 4) switch`; no se modificó ningún criterio de aceptación.
- Los fallos posteriores de los ejecutables eran consecuencia de que el build no había producido binaries, no fallos funcionales independientes.
- El TestChangeRequest de initialize amplía el assertion de capabilities `{}` a tools/resources exactos y conserva todas las pruebas de lifecycle.
- El journey usa el proceso real, un Artifact Store durable y contenido real; no usa mocks del router ni del store como evidencia única.
- Las tools no respaldadas no se simulan ni se anuncian.
- No se detectan componentes productivos huérfanos dentro del alcance de VS-021.

## Evidencia

- RED Governance: run previo del PR #25 con contratos `book-core` ausentes.
- Primer GREEN fallido: run `30223524954`, artifact `8637914883`.
- GREEN .NET: run `30223585480`, job `89850084750`.
- GREEN Governance: run `30223585460`.
- GREEN Plan Integrity: run `30223585462`.
- Artifact: `8637932832`.
- Digest: `sha256:33556d8b2ddf2037a67abd5b80298096f96df5c3a1a8025ae3e8273644c90acd`.
- `dotnet.book-core-integration`: PASS, exit code 0.
- `dotnet.mcp-initialize-integration`: PASS, exit code 0.

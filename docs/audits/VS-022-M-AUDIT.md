# VS-022 — Auditoría M

## Resultado

`PASS`

## M1 — Specification

- El servidor acotado es un proceso independiente: `BookStudio.Mcp.Authoring`.
- Surface activo exacto:
  - `book.draft.register`;
  - `book.draft.validate`.
- Surface reservado y no anunciado:
  - `book.plan.create`;
  - `book.scene.generate`;
  - `book.chapter.generate`;
  - `book.manuscript.assemble`.
- Initialize anuncia exactamente tools/resources.
- Register y validate publican schemas, annotations y `taskSupport = forbidden` coherentes con su semántica.
- La generación mediante IA se mantiene fuera del alcance hasta OpenCode y workflows durables.

## M2 — Implementation

- `BookStudio.Application.Authoring` contiene modelos, puerto y caso de uso provider-neutral.
- `DraftAuthoringService` depende de `IArtifactStore`, no de Infrastructure.
- `BookStudio.Mcp.Authoring` contiene únicamente composición, catálogo, schemas, routing y transformación MCP.
- `McpSession` acepta identidad de servidor configurable, conservando el constructor y la identidad por defecto de book-core.
- `BookAuthoringRuntime` crea el store perezosamente.
- Register usa `PutAsync` y no dispone de ruta de sobrescritura.
- Validate y resources/read verifican integridad antes de interpretar UTF-8.

## M3 — Tests

Los contratos estáticos verifican:

- archivos requeridos;
- surface activo/reservado;
- annotations diferenciadas;
- Application sin dependencia Infrastructure;
- ejecutable separado e identidad authoring;
- contrato CI y workflow.

El subprocess real verifica:

- workspace lazy;
- identidad `bookstudio-authoring`;
- capabilities exactas;
- tools/list exacto y ordenado;
- resources/list paginado;
- lectura de schema;
- registro inmutable de draft v1;
- validación y métricas de v1;
- lectura textual del resource;
- conflicto de versión;
- rechazo de scope cruzado;
- rechazo de control NUL;
- registro de v2;
- warnings de línea larga, espacios finales y tab;
- rechazo de tool reservada;
- EOF, exit 0, stdout limpio y stderr saneado.

Todos los journeys acumulativos permanecen en PASS.

## M4 — Security and Operations

- Registro limitado a 512 KiB UTF-8.
- Lectura/validación limitada a 1 MiB.
- Media types permitidos: text/markdown y text/plain.
- Project/artifact scope exige prefijo `{projectId}.draft.`.
- Se rechazan controles no permitidos, versiones no positivas, propiedades adicionales y límites inválidos.
- No hay egress, shell, IA, prompts, overwrite ni operaciones destructivas.
- Paths físicos, `.bookstudio`, contenido y excepciones no aparecen en respuestas o stderr.
- El servidor no crea workspace durante initialize/list.

Riesgos residuales:

- El scope usa el prefijo lógico porque el agregado durable Project aún no existe.
- La validación es determinista y estructural; no sustituye edición lingüística o auditoría narrativa.
- Generation/plan/assemble requieren sus slices y no cuentan como implementadas.

## M5 — Product Flow

```text
client launches BookStudio.Mcp.Authoring
→ initialize authoring identity
→ tools/list
→ book.draft.register
→ Application validates bounded text
→ Artifact Store publishes immutable version
→ book.draft.validate
→ integrity check + deterministic metrics/warnings
→ resources/read
→ next immutable version or safe domain error
→ EOF
```

## Meta-Audit

- RED confirmado en run `30224020639`: faltaba la implementación requerida.
- El primer GREEN funcional de .NET pasó, pero Governance detectó que los nuevos AGENTS.md no cumplían las secciones `## Allowed` / `## Forbidden`.
- La corrección fue exclusivamente documental; no se redujeron pruebas funcionales.
- El head `10c12115d9606b5aadc1edccda69af946517a335` supera Plan Integrity, Governance y .NET CI.
- El journey usa proceso y Artifact Store reales, sin mocks como evidencia externa.
- No se detectan handlers para tools reservadas ni componentes huérfanos dentro del alcance.

## Evidencia

- RED Plan Integrity: run `30224020651` PASS.
- RED Governance: run `30224020639` FAIL esperado.
- GREEN Plan Integrity: run `30224846906` PASS.
- GREEN Governance: run `30224846942` PASS.
- GREEN .NET: run `30224846907`, job `89853248964` PASS.
- Artifact: `8638266029`.
- Digest: `sha256:27df3f92bb30df943ecdf7b969ecda5f04cd59ee7da132995696e766e39e86cd`.
- `dotnet.book-authoring-integration`: PASS, exit code 0.
- `dotnet.mcp-initialize-integration`: PASS, exit code 0.
- Solution build: 0 warnings, 0 errors.

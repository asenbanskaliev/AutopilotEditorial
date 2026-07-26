# VS-022 — book-authoring Server

## IntentSpec

### Problema

El programa ya dispone de lifecycle MCP, consulta de artefactos y un Artifact Store durable, pero todavía no existe un servidor acotado de autoría. No se deben anunciar generación, revisión o ensamblado mediante IA antes de implementar OpenCode y los workflows correspondientes.

### Objetivo

Crear un proceso MCP independiente `BookStudio.Mcp.Authoring` con dos tools ejecutables y verificables:

1. `book.draft.register`;
2. `book.draft.validate`.

Ambas usan casos de uso Application reales y el Artifact Store durable. Las operaciones futuras de planificación y generación quedan reservadas, no anunciadas y no simuladas.

## Surface público

### Tools activas

- `book.draft.register`
- `book.draft.validate`

### Tools reservadas

- `book.plan.create`
- `book.scene.generate`
- `book.chapter.generate`
- `book.manuscript.assemble`

Las tools reservadas pueden existir como constantes para impedir deriva de nombres, pero no deben aparecer en `tools/list` ni tener handlers.

## Servidor acotado

Nuevo ejecutable:

```text
src/BookStudio.Mcp.Authoring/BookStudio.Mcp.Authoring.csproj
```

Identidad initialize:

```json
{
  "name": "bookstudio-authoring",
  "title": "BookStudio Authoring MCP",
  "version": "<assembly-version>"
}
```

Capabilities exactas:

```json
{
  "tools": { "listChanged": false },
  "resources": { "subscribe": false, "listChanged": false }
}
```

No se anuncian prompts, logging, completions, sampling, elicitation, roots, tasks ni experimental.

## Tool `book.draft.register`

### Input

```json
{
  "projectId": "project-slug",
  "payload": {
    "artifactId": "project-slug.draft.chapter-01",
    "expectedVersion": 1,
    "mediaType": "text/markdown",
    "content": "# Chapter 1\n..."
  }
}
```

Reglas:

- `projectId`: `^[a-z0-9][a-z0-9-]{0,63}$`.
- `artifactId`: lowercase slug de hasta 128 caracteres.
- El artifact debe comenzar por `{projectId}.draft.`.
- `expectedVersion >= 1`.
- media types permitidos: `text/markdown` y `text/plain`.
- contenido UTF-8 no vacío y máximo 512 KiB.
- additional properties rechazadas.

Semántica:

- publica exactamente una versión inmutable;
- usa `IArtifactStore.PutAsync`;
- una versión ya ocupada produce error de dominio `draft_version_conflict`;
- no sobrescribe ni modifica versiones anteriores;
- devuelve referencia lógica, SHA-256, longitud, media type y URI.

Annotations:

```json
{
  "readOnlyHint": false,
  "destructiveHint": false,
  "idempotentHint": false,
  "openWorldHint": false
}
```

`execution.taskSupport = forbidden`.

## Tool `book.draft.validate`

### Input

```json
{
  "projectId": "project-slug",
  "payload": {
    "artifactId": "project-slug.draft.chapter-01",
    "version": 1,
    "maximumLineLength": 120
  }
}
```

Reglas:

- mismos límites de project/artifact scope;
- versión positiva;
- `maximumLineLength` entre 40 y 240, default 120.

Semántica:

- verifica integridad del artefacto;
- exige media type textual y UTF-8 válido;
- máximo de lectura 1 MiB;
- calcula caracteres, palabras, líneas, párrafos y headings Markdown;
- detecta contenido vacío, líneas excesivas, espacios finales, tabs y NUL/control characters no permitidos;
- devuelve warnings deterministas y no modifica el artefacto.

Annotations:

```json
{
  "readOnlyHint": true,
  "destructiveHint": false,
  "idempotentHint": true,
  "openWorldHint": false
}
```

`execution.taskSupport = forbidden`.

## Resultado estructurado

```json
{
  "resultType": "complete | failed",
  "operationId": "stable bounded identifier",
  "artifactRefs": [],
  "warnings": [],
  "data": {},
  "error": null
}
```

Errores esperados se devuelven como `CallToolResult` con `isError: true`. Params MCP malformados y tool desconocida siguen siendo errores JSON-RPC.

## Resources

Schemas estáticos:

- `book://schemas/book-authoring/tool-result`;
- `book://schemas/book-authoring/draft-register-input`;
- `book://schemas/book-authoring/draft-register-output`;
- `book://schemas/book-authoring/draft-validate-input`;
- `book://schemas/book-authoring/draft-validate-output`.

Resource template:

```text
book://project/{projectId}/artifact/{artifactId}/versions/{version}
```

El servidor authoring puede leer únicamente artefactos `{projectId}.draft.*` y devuelve text hasta 1 MiB. No devuelve blobs binarios ni paths físicos.

## Application

Nuevo puerto:

```text
IDraftAuthoringService
```

Métodos:

- `RegisterAsync(DraftRegistrationCommand)`;
- `ValidateAsync(DraftValidationQuery)`;
- `ReadResourceAsync(DraftResourceQuery)`.

Application valida scope, límites y contenido. No referencia Infrastructure.

## Runtime y composición

`BookAuthoringRuntime` crea perezosamente `FileArtifactStore` y `DraftAuthoringService`.

Workspace root:

1. `--workspace-root`;
2. `BOOKSTUDIO_WORKSPACE_ROOT`;
3. default local de plataforma.

Initialize y listados no crean directorios.

## Seguridad

- sin egress;
- sin ejecución de procesos;
- sin IA ni prompts;
- sin sobrescritura;
- sin paths físicos;
- contenido limitado antes de persistir;
- controles Unicode/UTF-8;
- mensajes de error saneados;
- stdout exclusivo para JSON-RPC.

## TDD Dual

### RED-I

Faltan:

- modelos y servicio Application;
- proyecto MCP authoring;
- catálogo, schemas y router;
- identidad configurable del servidor;
- runtime lazy;
- proyecto de integración;
- arquitectura y CI.

### RED-E

No existe proceso `BookStudio.Mcp.Authoring`; por tanto no puede inicializarse, listar tools, registrar un draft ni validarlo.

### GREEN-I

- contracts, build y architecture fitness PASS;
- schemas y annotations exactos;
- Application sin dependencia Infrastructure;
- CI contract registrado.

### GREEN-E

Proceso real verifica:

```text
initialize
→ tools/list
→ resources/list
→ register draft v1
→ validate v1
→ read resource
→ version conflict
→ project scope rejection
→ invalid UTF-8/control input rejection
→ register v2
→ validate warnings
→ reserved-tool rejection
→ lazy workspace
→ EOF
```

## Auditoría M

- M1: surface activo/reservado y schemas.
- M2: separación Application / protocol adapter / Infrastructure.
- M3: subprocess, store real, conflictos y adversarial inputs.
- M4: límites, no overwrite, no paths ni stubs.
- M5: register → validate → resource → next version.

## Definition of Done

- SPEC_READY.
- DUAL_RED_CONFIRMED.
- DUAL_GREEN.
- NO_ORPHANS_PASS.
- M_AUDIT_PASS.
- RETROSPEC_SYNCED.

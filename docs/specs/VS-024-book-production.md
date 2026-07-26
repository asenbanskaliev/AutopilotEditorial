# VS-024 — book-production Server

## IntentSpec

### Problema

BookStudio puede registrar y auditar drafts, pero no dispone de un contrato productivo que agrupe artefactos inmutables en una release reproducible ni de un preflight determinista que verifique sus fuentes. Renderizado, assets visuales avanzados y publicación todavía no tienen adapters reales.

### Objetivo

Crear un proceso MCP independiente `BookStudio.Mcp.Production` con dos tools reales:

1. `book.release.prepare`;
2. `book.preflight.run`.

La primera publica un manifiesto de release inmutable; la segunda lo valida y verifica todas sus fuentes sin modificar nada.

## Surface público

### Tools activas

- `book.release.prepare`
- `book.preflight.run`

### Tools reservadas

- `book.asset.register`
- `book.render.preview`
- `book.render.final`
- `book.publish.package`

Las tools reservadas no se anuncian ni tienen handlers.

## Servidor acotado

Ejecutable:

```text
src/BookStudio.Mcp.Production/BookStudio.Mcp.Production.csproj
```

Identidad:

```json
{
  "name": "bookstudio-production",
  "title": "BookStudio Production MCP"
}
```

Capabilities exactas: tools y resources.

## Tool `book.release.prepare`

Input:

```json
{
  "projectId": "demo",
  "payload": {
    "releaseId": "proof-01",
    "expectedVersion": 1,
    "title": "Demo Book",
    "language": "es-ES",
    "sources": [
      {"role":"manuscript","artifactId":"demo.draft.manuscript","version":1}
    ]
  }
}
```

Reglas:

- projectId y releaseId son slugs acotados;
- artifact de salida: `{projectId}.release.{releaseId}`;
- expectedVersion positivo;
- title 1..200 caracteres seguros;
- language BCP-47 simplificado;
- 1..50 sources;
- roles permitidos: manuscript, cover, metadata, interior-pdf, epub, supplemental;
- exactamente un manuscript;
- cada source pertenece al proyecto, existe, verifica integridad y no referencia la propia release;
- no se admiten referencias duplicadas.

Semántica:

- normaliza y ordena sources;
- publica JSON canónico e inmutable;
- media type `application/vnd.bookstudio.release-manifest+json`;
- una versión ocupada devuelve `release_version_conflict`;
- no renderiza ni copia fuentes.

Annotations: write, non-destructive, non-idempotent, closed-world, taskSupport forbidden.

## Tool `book.preflight.run`

Input:

```json
{
  "projectId": "demo",
  "payload": {
    "releaseArtifactId": "demo.release.proof-01",
    "version": 1,
    "profile": "release-basic"
  }
}
```

Semántica:

- verifica integridad y parsea el manifiesto;
- valida schemaVersion, project scope, release ID y sources;
- verifica existencia e integridad de cada source;
- valida media type según role;
- devuelve checks deterministas y decisión PASS/BLOCKED;
- no modifica release, sources, locks ni estado de publicación.

Compatibilidad mínima:

- manuscript: text/markdown o text/plain;
- cover: image/png, image/jpeg o image/svg+xml;
- metadata: application/json;
- interior-pdf: application/pdf;
- epub: application/epub+zip;
- supplemental: cualquier media type no vacío.

Annotations: read-only, non-destructive, idempotent, closed-world, taskSupport forbidden.

## Checks de preflight

- `release.schema_version`;
- `release.project_scope`;
- `release.manuscript_present`;
- `release.no_duplicate_sources`;
- `release.sources_available`;
- `release.sources_integrity`;
- `release.role_media_compatibility`.

## Resources

Schemas:

```text
book://schemas/book-production/*
```

Perfil:

```text
book://production/profiles/release-basic
```

No se exponen bytes de fuentes ni paths físicos.

## Application

Puerto:

```text
IReleaseProductionService
```

Métodos:

- `PrepareAsync(ReleasePreparationCommand)`;
- `RunPreflightAsync(ReleasePreflightQuery)`.

Application depende de IArtifactStore y no referencia Infrastructure.

## Runtime

`BookProductionRuntime` crea perezosamente store y servicio. Initialize/list no crean workspace.

## Seguridad

- sin egress, procesos externos, render, publicación o overwrite;
- manifiesto máximo 1 MiB;
- todos los sources integrity-checked;
- sin paths, bytes fuente, secretos o excepciones crudas;
- roles y media types allow-listed;
- no se acepta una release como source de sí misma.

## TDD Dual

### RED-I

Faltan Application service, proceso production, schemas, catálogo, router, runtime, integración, arquitectura y CI.

### RED-E

No existe flujo authoring→release.prepare→preflight.

### GREEN-E

```text
authoring register manuscript + incompatible cover fixture
→ production lazy initialize/list
→ release.prepare good
→ preflight good PASS
→ version conflict
→ release.prepare incompatible
→ preflight incompatible BLOCKED
→ scope rejection
→ reserved render rejection
→ preflight no mutation
→ EOF
```

## Definition of Done

- SPEC_READY;
- DUAL_RED_CONFIRMED;
- DUAL_GREEN;
- NO_ORPHANS_PASS;
- M_AUDIT_PASS;
- RETROSPEC_SYNCED.

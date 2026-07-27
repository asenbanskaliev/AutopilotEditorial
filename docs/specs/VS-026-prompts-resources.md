# VS-026 — Prompts and Resources

## IntentSpec

### Problema

Los cinco bounded MCP servers ya exponen tools y resources reales, pero todavía no ofrecen prompts MCP user-controlled. Los clientes no disponen de workflows guiados, versionados y descubribles para combinar las tools activas sin depender de conocimiento externo o de comandos manuales.

### Objetivo

Añadir soporte MCP 2025-11-25 para:

- `prompts/list` con paginación opaca;
- `prompts/get` con argumentos string estrictos;
- capability `prompts.listChanged = false`;
- un prompt v1 ejecutable y honesto por bounded context;
- un resource JSON versionado generado desde la misma definición que cada prompt.

Los prompts devuelven mensajes textuales para el cliente/LLM. No llaman tools, no hacen sampling y no ejecutan modelos.

## Catálogo público

| Server | Prompt | Resource |
|---|---|---|
| book-core | `book.core.inspect-artifact.v1` | `book://prompts/book-core/inspect-artifact/v1` |
| book-authoring | `book.authoring.validate-draft.v1` | `book://prompts/book-authoring/validate-draft/v1` |
| book-quality | `book.quality.assess-draft.v1` | `book://prompts/book-quality/assess-draft/v1` |
| book-production | `book.production.preflight-release.v1` | `book://prompts/book-production/preflight-release/v1` |
| book-ops | `book.ops.inspect-readiness.v1` | `book://prompts/book-ops/inspect-readiness/v1` |

Los nombres y URIs incluyen versión explícita. Una revisión incompatible requiere `v2`; nunca se modifica silenciosamente el significado de `v1`.

## Shared protocol support

Nuevo bounded support package dentro del assembly MCP compartido:

```text
src/BookStudio.Mcp/Prompts/
```

Componentes:

- `McpPromptModels.cs`;
- `VersionedMcpPrompt.cs`;
- `VersionedMcpPromptCatalog.cs`;
- `McpPromptDispatcher.cs`;
- `PromptArgumentRules.cs`.

Responsabilidades:

- modelos JSON conformes para Prompt, PromptArgument, PromptMessage y GetPromptResult;
- definición inmutable de nombre, versión, argumentos, resource URI y template;
- validación exacta de argumentos;
- renderizado acotado y determinista;
- list/get con JSON-RPC Invalid params para cursor, nombre o argumentos inválidos;
- resource JSON canónico derivado de la misma definición;
- fingerprint estable para paginación.

## Capability contract

Los cinco servidores anuncian exactamente:

```json
{
  "prompts": { "listChanged": false },
  "resources": { "subscribe": false, "listChanged": false },
  "tools": { "listChanged": false }
}
```

Continúan ausentes:

- logging;
- completions;
- sampling;
- roots;
- tasks;
- experimental.

## `prompts/list`

- params ausentes o `{ "cursor": "..." }`;
- cursor opaco, bounded y ligado a scope/fingerprint;
- orden lexicográfico determinista;
- máximo 20 prompts por página;
- cada bounded server devuelve exactamente un prompt v1;
- no crea workspace ni inicializa runtime de datos.

Prompt definition:

```json
{
  "name": "book.core.inspect-artifact.v1",
  "title": "Inspect immutable artifact",
  "description": "...",
  "arguments": [
    {"name":"projectId","title":"Project ID","description":"...","required":true}
  ]
}
```

## `prompts/get`

Params:

```json
{
  "name": "book.core.inspect-artifact.v1",
  "arguments": {
    "projectId": "demo",
    "artifactId": "demo.chapter-01",
    "version": "1"
  }
}
```

Reglas comunes:

- params contienen exactamente `name` y opcional `arguments`;
- arguments es object de string→string;
- nombres adicionales rechazados;
- strings no vacíos, sin controles y acotados;
- versión positiva canónica;
- prompt desconocido o argumento inválido: JSON-RPC `-32602`;
- respuesta contiene description y al menos un PromptMessage;
- role permitido: `user` o `assistant`;
- content es `TextContent` bounded;
- no se devuelven secretos, paths ni contenido de artefactos.

## Prompt `book.core.inspect-artifact.v1`

Argumentos requeridos:

- projectId;
- artifactId;
- version.

Scope:

- projectId slug;
- artifactId comienza por `{projectId}.`;
- versión positiva.

Mensaje guía al cliente a:

1. usar `book.artifact.get` con texto acotado;
2. describir metadata e integridad;
3. no inventar contenido ausente;
4. usar compare solo cuando el usuario proporcione otra versión.

## Prompt `book.authoring.validate-draft.v1`

Argumentos requeridos:

- projectId;
- artifactId;
- version.

Scope: `{projectId}.draft.*`.

Mensaje guía a:

1. usar `book.draft.validate`;
2. explicar métricas/warnings;
3. no registrar una versión nueva sin autorización separada;
4. no presentar la validación estructural como edición lingüística completa.

## Prompt `book.quality.assess-draft.v1`

Argumentos requeridos:

- projectId;
- artifactId;
- version.

Scope: `{projectId}.draft.*`.

Mensaje guía a:

1. usar `book.audit.run`;
2. usar `book.gate.evaluate` con `draft-basic`;
3. diferenciar warn/fail y PASS/BLOCKED;
4. no llamar repair/memory reservadas.

## Prompt `book.production.preflight-release.v1`

Argumentos requeridos:

- projectId;
- releaseArtifactId;
- version.

Scope: `{projectId}.release.*`.

Mensaje guía a:

1. usar `book.preflight.run` con `release-basic`;
2. reportar checks y blockingReasons;
3. no afirmar cumplimiento KDP completo;
4. no llamar render/package/publish reservadas.

## Prompt `book.ops.inspect-readiness.v1`

Sin argumentos.

Mensaje guía a:

1. usar `book.ops.status`;
2. usar `book.ops.diagnostics` cuando no esté ready o se necesite detalle;
3. explicar available/reserved;
4. no llamar controles Autopilot reservados.

## Versioned prompt resources

Cada prompt añade un resource listado y legible:

```json
{
  "schemaVersion": "1.0.0",
  "promptVersion": "1",
  "name": "...v1",
  "title": "...",
  "description": "...",
  "arguments": [],
  "messages": [
    {"role":"user","content":{"type":"text","text":"template con {{argument}}"}}
  ]
}
```

Media type:

```text
application/vnd.bookstudio.prompt-template+json
```

El resource y `prompts/list/get` se generan desde la misma `VersionedMcpPrompt`; se prueba paridad de nombre, argumentos y template.

## Server integration

Cada bounded server añade `Book*PromptCatalog.cs` y:

- capability prompts;
- dispatch de prompts/list/get;
- prompt resource en resources/list/read;
- fingerprint de resources actualizado;
- instructions que mencionan prompts user-controlled;
- no inicialización de runtime para list/get/resource estático.

## Security

- templates son constantes confiables, no contenido externo;
- argumentos se insertan después de validación y sin interpretación como instrucciones del servidor;
- ningún prompt incluye contenido de archivos, secrets o paths;
- máximo de argumento: 256 caracteres salvo IDs más acotados;
- máximo de mensaje renderizado: 4096 caracteres;
- no sampling, elicitation, roots, completions o egress;
- stdout exclusivo para JSON-RPC;
- recursos prompt inmutables y versionados.

## TDD Dual

### RED-I

Faltan modelos/dispatcher compartidos, cinco catálogos, capabilities, resources, conformance integration, arquitectura y CI.

### RED-E

Ninguno de los cinco procesos responde a prompts/list/get ni anuncia prompts.

### GREEN-E

Un integration executable lanza los cinco procesos y verifica para cada uno:

```text
initialize exact prompts/resources/tools
→ prompts/list exact v1
→ prompts/get valid
→ prompt resource read
→ definition/resource/get parity
→ missing argument rejection
→ extra argument rejection
→ invalid scope/version rejection
→ unknown prompt rejection
→ lazy workspace unchanged
→ EOF
```

Además se reejecutan los journeys acumulativos con la nueva capability y resources versionados.

## TestChangeRequest

Los tests acumulativos actuales fijan exactamente dos capabilities y, en algunos casos, tamaños exactos de resources. Se autoriza actualizarlos para:

- exigir prompts como tercera capability;
- retirar prompts de la lista de capabilities prohibidas;
- ajustar páginas de resources por el nuevo prompt resource;
- mantener todas las expectativas previas de tools, seguridad, lazy runtime y EOF.

La modificación añade cobertura; no elimina ningún requisito funcional o de seguridad.

## Definition of Done

- SPEC_READY;
- DUAL_RED_CONFIRMED;
- DUAL_GREEN;
- MCP_PROMPTS_CONFORMANCE_PASS;
- RESOURCE_PARITY_PASS;
- NO_ORPHANS_PASS;
- M_AUDIT_PASS;
- RETROSPEC_SYNCED.

# VS-026 — Auditoría M

## Resultado

`PASS`

## M1 — Specification

- Se añadió soporte MCP 2025-11-25 para `prompts/list` y `prompts/get`.
- Los cinco bounded servers anuncian exactamente tools, resources y prompts.
- Cada servidor expone un único prompt v1 explícitamente versionado:
  - `book.core.inspect-artifact.v1`;
  - `book.authoring.validate-draft.v1`;
  - `book.quality.assess-draft.v1`;
  - `book.production.preflight-release.v1`;
  - `book.ops.inspect-readiness.v1`.
- Cada prompt dispone de un resource canónico generado desde la misma definición.
- Los prompts son user-controlled templates: no ejecutan tools, sampling, modelos, roots, completions ni egress.
- Una revisión incompatible requiere un nombre v2 y una URI v2; v1 permanece inmutable.

## M2 — Implementation

- `McpPromptModels` contiene los contratos JSON tipados.
- `VersionedMcpPrompt` conserva nombre, versión, URI, argumentos, template, renderer y resource JSON inmutables.
- Nombre, versión y URI se validan conjuntamente para impedir deriva del contrato público.
- `VersionedMcpPromptCatalog` ordena, indexa, calcula fingerprint y expone prompts/resources sin duplicados.
- `McpPromptDispatcher` valida list/get, cursores, nombres y argumentos string acotados.
- `PromptArgumentRules` aplica project scope, draft/release scope y versión canónica.
- `PromptEnabledFeatureRouter` añade capability prompts, fusiona resources y delega el comportamiento bounded previo.
- Las cinco composition roots usan el decorator y su catálogo correspondiente.
- Ningún runtime de datos se inicializa para initialize, prompts/list, prompts/get o lectura del resource estático.

## M3 — Tests

Los contratos estáticos verifican:

- archivos shared requeridos;
- nombres y URIs explícitos en cada catálogo;
- decorator común y composición en los cinco procesos;
- separación dispatcher/rendering;
- contrato CI y workflow;
- solución y arquitectura sincronizadas.

El journey de conformance lanza los cinco procesos reales y verifica para cada servidor:

- identidad preservada;
- capabilities exactas `prompts`, `resources`, `tools`;
- `prompts.listChanged = false`;
- prompts/list con el único prompt v1 correcto;
- prompts/get válido;
- resource listado y legible;
- paridad nombre/argumentos/template entre definición, get y resource;
- argumento ausente, adicional, scope inválido y versión inválida rechazados;
- prompt desconocido rechazado;
- workspace perezoso sin creación o mutación;
- stdout exclusivo JSON-RPC, stderr saneado y EOF con exit 0.

Todos los journeys acumulativos fueron actualizados mediante TCR y permanecen en PASS.

## M4 — Security and Operations

- Máximo 20 prompts por página.
- Máximo 16 argumentos.
- Nombres de argumento limitados a 64 caracteres.
- Valores limitados a 256 caracteres y sin controles.
- Mensaje renderizado limitado a 4096 caracteres.
- Cursores opacos ligados a scope y fingerprint.
- Templates constantes confiables; argumentos se validan antes del renderizado.
- No se incluyen bytes de artefactos, rutas físicas, secretos o contenido externo.
- No sampling, tool execution, models, shell, network, roots, completions o tasks.
- Los resources son inmutables, versionados y con media type dedicado.
- Governance conserva ahora el log completo de unittest como evidencia diagnóstica sin alterar el resultado por `pipefail`.

Riesgos residuales:

- Los prompts describen workflows guiados, pero su ejecución depende del cliente/LLM.
- Nuevas versiones requieren mantenimiento explícito de catálogo y conformance.
- No existe todavía negociación de prompts por perfiles de agente; pertenece a F3.

## M5 — Product Flow

```text
client initialize
→ discover prompts capability
→ prompts/list
→ select bounded prompt v1
→ prompts/get with validated arguments
→ receive deterministic user message
→ client decides whether to invoke active tools
→ read matching immutable prompt resource when provenance is required
→ no automatic execution or workspace mutation
```

## TestChangeRequests

### TCR-026-001

Aprobó añadir prompts como tercera capability y ajustar únicamente conteos/paginación por el nuevo resource, conservando todos los checks previos.

### TCR-026-002

Aprobó un decorator compartido en lugar de duplicar dispatch, capability merge y resource merge en cinco routers.

### TCR-026-003

Aprobó que el dispatcher posea validación/dispatch y que `VersionedMcpPrompt.Render` posea la construcción tipada de mensajes y texto.

## Meta-Audit

- RED-I/RED-E iniciales confirmados en el head `f4ba38c91f53a30d5c745a6cf259e6cf5994a74b`:
  - Governance run `30254045195` FAIL esperado;
  - .NET CI run `30254045299` FAIL esperado;
  - Plan Integrity run `30254045213` PASS.
- Los fallos intermedios se debieron a contratos acumulativos legítimos y a source-location coupling, no a reducción de requisitos.
- La conformance nueva pasó antes de actualizar regresiones acumulativas, demostrando comportamiento externo real.
- El GREEN final ejecutó todas las regresiones, no solo el nuevo journey.
- No existen prompts simulados, handlers reservados ni llamadas automáticas a modelos/tools.
- No hay componentes huérfanos dentro del alcance.

## Evidencia GREEN

- Head funcional: `52382ce100c3c8bd258ce1e234dd739bc1f1f79b`.
- Plan Integrity: run `30259870843` PASS.
- Governance: run `30259870936` PASS.
- Governance artifact: `8650426627`.
- Governance digest: `sha256:4ae97e701f4f9457b63b3659c439aabc21348107800749347c1ddea6d2fb1569`.
- .NET CI: run `30259870849`, job `89956987756` PASS.
- .NET artifact: `8650454508`.
- .NET digest: `sha256:3510c6bd2faae005ce7c1a9d9faff2fffedf5c5a4b3753d9aa3a9959cbae48d6`.
- `dotnet.prompts-resources-integration`: PASS.
- Todos los contratos normalizados acumulativos: PASS, exit code 0.

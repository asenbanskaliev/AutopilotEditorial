# VS-027 — MCP Conformance

## IntentSpec

### Problema

Los cinco bounded MCP servers disponen de journeys funcionales, pero no existe una suite transversal que demuestre de forma repetible que todos cumplen el mismo contrato JSON-RPC/MCP frente a entradas malformadas, lifecycle incorrecto, límites de transporte y mutaciones fuzzed. Un servidor puede pasar su happy path y aun así bloquearse, cerrarse, filtrar datos o responder con códigos inconsistentes.

### Objetivo

Crear una suite subprocess independiente y versionada que ejecute:

- `BookStudio.Mcp`;
- `BookStudio.Mcp.Authoring`;
- `BookStudio.Mcp.Quality`;
- `BookStudio.Mcp.Production`;
- `BookStudio.Mcp.Ops`.

La suite no llama routers ni servicios directamente. Cada proceso se prueba por stdin/stdout/stderr real.

## Artefactos

```text
tests/BookStudio.Tests.McpConformance/
├── AGENTS.md
├── BookStudio.Tests.McpConformance.csproj
├── Program.cs
├── McpConformanceRunner.cs
├── McpProcessDriver.cs
└── Corpus/mcp-conformance-v1.json
```

El corpus JSON es inmutable dentro de v1. Cambios incompatibles requieren `mcp-conformance-v2.json`.

## Procesos objetivo

Cada descriptor declara:

- assembly;
- server name;
- server title;
- workspace root temporal inexistente;
- capabilities esperadas.

Capabilities exactas tras VS-026:

```json
{
  "prompts": {"listChanged": false},
  "resources": {"subscribe": false, "listChanged": false},
  "tools": {"listChanged": false}
}
```

## Contratos de transporte

La suite verifica por proceso:

- UTF-8 newline-delimited JSON-RPC;
- línea vacía o whitespace → `-32600`;
- JSON truncado, trailing comma o comentario → `-32700`;
- objeto máximo 1 MiB;
- mensaje superior a 1 MiB → `-32600`, id null, proceso continúa;
- profundidad superior al límite → `-32700`;
- stdout contiene únicamente una respuesta JSON por request con respuesta;
- stderr contiene solo códigos diagnósticos `[A-Za-z0-9_-]`, máximo 96 caracteres;
- EOF finaliza con exit code 0 y sin stdout pendiente.

## Contratos JSON-RPC

- raíz no-object → `-32600`;
- `jsonrpc` ausente, no-string o distinto de `2.0` → `-32600`;
- `method` ausente, no-string, vacío, con controles o >128 → `-32600`;
- id boolean, object o array → `-32600`, id null;
- params presente y no-object:
  - request → `-32602`;
  - notification → sin respuesta y diagnóstico seguro;
- método desconocido ready → `-32601`;
- request id reutilizado → `-32600`;
- respuestas preservan id string o entero legible;
- error siempre contiene `jsonrpc=2.0`, code entero y message no vacío.

## Lifecycle MCP

Por proceso:

```text
start
→ ping permitido pre-initialize
→ feature request pre-initialize = -32002
→ initialize válido
→ identidad y protocolo
→ capabilities exactas
→ duplicate initialize = -32600
→ notifications/initialized
→ duplicate initialized notification sin respuesta
→ tools/list, resources/list y prompts/list válidos
→ malformed ready corpus
→ deterministic fuzz
→ oversize recovery
→ final ping
→ EOF
```

También se verifica:

- initialize como notification no produce respuesta;
- notifications/initialized con id es inválido;
- unknown notification no produce respuesta;
- el proceso continúa operativo tras cada bloque de errores.

## Corpus v1

El corpus contiene casos declarativos con:

- `id` estable;
- `phase`: `created` o `ready`;
- payload raw;
- código esperado;
- id esperado cuando sea legible.

El runner rechaza:

- schemaVersion desconocida;
- IDs duplicados;
- phases desconocidas;
- códigos fuera del conjunto permitido;
- payloads que superen el límite salvo el caso específico de oversize generado por código.

## Deterministic fuzz

- seed fija: `27027`;
- 128 casos por proceso;
- variantes generadas solo dentro de categorías con resultado determinista:
  - jsonrpc ausente/incorrecto;
  - method ausente/no-string/vacío/demasiado largo;
  - id inválido;
  - params no-object;
- IDs únicos y payload acotado;
- código esperado calculado por variante;
- ping de supervivencia cada 16 casos;
- hash SHA-256 del stream de casos registrado en salida para reproducibilidad.

No se usa random criptográfico ni información del entorno.

## No-crash / no-hang

- timeout por lectura: 10 segundos;
- timeout de cierre: 20 segundos;
- una respuesta ausente, extra o no JSON falla el journey;
- cualquier exit prematuro falla;
- después de corpus, fuzz y oversize se exige ping válido;
- no se permite retry que oculte un crash.

## Seguridad

- token secreto canario enviado en una notification desconocida;
- el token no puede aparecer en stdout o stderr;
- workspace root y nombres de ficheros internos no pueden aparecer;
- initialize/list/corpus/fuzz no crean workspace;
- no se ejecutan tools con efectos;
- no hay red, shell adicional, modelos o egress;
- el corpus no incluye secretos reales.

## Reporting

Salida única de éxito:

```text
MCP_CONFORMANCE_PASS servers=5 corpus=<n> fuzz=640 seed=27027 sha256=<hash>
```

En error, stderr del test puede contener stack trace porque es un test host, pero nunca contenido secreto de procesos. Los servidores conservan stderr saneado.

## CI

Contrato:

```text
dotnet.mcp-conformance-integration
```

Workflow ejecuta el proyecto después de prompts/resources y genera:

```text
artifacts/ci/dotnet-mcp-conformance-integration.json
```

## TDD Dual

### RED-I

Faltan proyecto, harness, driver, corpus, governance contract, solución, arquitectura y CI.

### RED-E

No existe una ejecución única que pruebe los cinco procesos contra corpus malformado y fuzz determinista.

### GREEN-E

Los cinco procesos completan lifecycle, corpus, fuzz, oversize recovery, no-leak, lazy workspace y EOF.

## Definition of Done

- SPEC_READY;
- DUAL_RED_CONFIRMED;
- DUAL_GREEN;
- MCP_CONFORMANCE_PASS;
- MALFORMED_INPUT_PASS;
- DETERMINISTIC_FUZZ_PASS;
- NO_ORPHANS_PASS;
- M_AUDIT_PASS;
- RETROSPEC_SYNCED.

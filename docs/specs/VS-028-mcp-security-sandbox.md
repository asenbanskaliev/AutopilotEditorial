# VS-028 — MCP Security Sandbox

## IntentSpec

### Problema

BookStudio ya confina artefactos dentro de `.bookstudio/artifacts`, valida IDs y rechaza enlaces dentro del store. Sin embargo, los procesos MCP todavía aceptan cualquier workspace local canonicalizable, mantienen un límite individual de 256 MiB y no aplican cuotas globales de bytes o ficheros. Una configuración errónea podría apuntar al root del sistema, a un enlace existente o permitir crecimiento no acotado del store.

### Objetivo

Aplicar una política de sandbox común a los cinco MCP servers con enforcement en dos capas:

1. **Host admission** antes de iniciar JSON-RPC.
2. **Artifact Store quotas** dentro de la transacción de escritura.

La política debe ser uniforme, configurable mediante argumentos explícitos, fail-closed y verificable mediante procesos reales.

## Procesos incluidos

- `BookStudio.Mcp`;
- `BookStudio.Mcp.Authoring`;
- `BookStudio.Mcp.Quality`;
- `BookStudio.Mcp.Production`;
- `BookStudio.Mcp.Ops`.

## Host policy

`McpHostOptions` incorpora:

```text
WorkspaceRoot
MaximumArtifactBytes
MaximumStoreBytes
MaximumStoreFiles
```

Argumentos:

```text
--workspace-root <path>
--max-artifact-bytes <positive integer>
--max-store-bytes <positive integer>
--max-store-files <positive integer>
```

También se aceptan formas `--name=value`.

Defaults MCP strict:

```text
MaximumArtifactBytes = 16 MiB
MaximumStoreBytes = 1 GiB
MaximumStoreFiles = 100000
```

Bounds:

- maximum artifact: 1 KiB .. 256 MiB;
- maximum store: 64 KiB .. 16 GiB;
- maximum files: 16 .. 1,000,000;
- store bytes debe ser >= artifact bytes;
- cada opción puede declararse una sola vez;
- números decimales canónicos, sin signo, whitespace ni unidades.

## Workspace root admission

Nuevo componente compartido:

```text
McpWorkspaceSandboxPolicy
```

Debe:

- canonicalizar con `Path.GetFullPath`;
- rechazar string vacío, controles o longitud >4096;
- rechazar filesystem root (`/`, `C:\`, etc.);
- rechazar existing regular file;
- rechazar UNC y device paths en Windows;
- recorrer todos los componentes existentes desde el filesystem root hasta el workspace y rechazar symbolic links/reparse points;
- aceptar un directorio inexistente bajo una cadena de padres locales no enlazados;
- no crear el workspace durante parsing/admission;
- devolver mensajes genéricos; el proceso solo emite `MCP_INVALID_HOST_OPTIONS` y exit 2.

## Artifact Store policy

`FileArtifactStoreOptions` añade:

```text
MaximumStoreBytes
MaximumStoreFiles
```

Defaults de Infrastructure permanecen compatibles con tests existentes:

```text
MaximumArtifactBytes = 256 MiB
MaximumStoreBytes = 4 GiB
MaximumStoreFiles = 250000
```

Los MCP runtimes pasan los límites strict de `McpHostOptions`.

## Quota enforcement

Nuevo error provider-neutral:

```text
ArtifactStoreQuotaExceededException
```

Propiedades:

- quota: `bytes` o `files`;
- maximum;
- observed;
- mensaje sin paths.

`FileArtifactStore.PutAsync`:

1. valida límite individual durante streaming;
2. serializa la sección de quota/publish mediante un write gate global por store instance;
3. inspecciona únicamente ficheros bajo StoreRoot;
4. rechaza cualquier enlace/reparse encontrado;
5. cuenta bytes y ficheros con aritmética checked;
6. incluye el temp content y una reserva de manifest acotada;
7. rechaza antes de publicar si bytes o files superarían la policy;
8. limpia temp files en error;
9. no publica manifest ni versión parcial;
10. mantiene deduplicación e inmutabilidad.

Reserva de manifest:

```text
64 KiB y 1 fichero
```

Una escritura rechazada no consume versión. Tras elevar la cuota, la misma `expectedVersion` debe poder publicarse.

## Application error mapping

Los write use cases mapean quota global a códigos estables:

- authoring: `artifact_store_quota_exceeded`;
- production release: `artifact_store_quota_exceeded`.

No se devuelve maximum, observed, workspace o filesystem path al MCP client.

El límite individual mantiene sus códigos existentes (`draft_too_large`, etc.).

## Runtime composition

Los runtimes que crean `FileArtifactStore` reciben `McpHostOptions` o una policy equivalente, no solo un string root:

- BookCoreRuntime;
- BookAuthoringRuntime;
- BookQualityRuntime;
- BookProductionRuntime.

Ops utiliza root admission aunque su readiness probe no cree Artifact Store.

Initialize, list, prompts y resources estáticos continúan sin crear workspace.

## Security policy resource

Cada servidor añade el resource estático:

```text
book://security/sandbox-policy
```

Media type:

```text
application/vnd.bookstudio.sandbox-policy+json
```

Contenido público, sin path:

```json
{
  "schemaVersion":"1.0.0",
  "mode":"strict-local",
  "maximumArtifactBytes":16777216,
  "maximumStoreBytes":1073741824,
  "maximumStoreFiles":100000,
  "workspaceRules":["not-filesystem-root","local-path","no-existing-links","no-existing-file"],
  "storeRules":["confined-artifacts","no-links","immutable-versions","quota-before-publish"]
}
```

El resource refleja los límites efectivos del proceso y se genera desde `McpHostOptions`.

Se integra mediante un decorator shared para no duplicar resources/list/read en cinco routers.

## Threat scenarios

### Host rejection

- `/` o drive root;
- existing file como workspace;
- symlink workspace;
- parent symlink + child inexistente;
- duplicate option;
- unknown option;
- zero, negative, plus-sign, whitespace, unit suffix o overflow;
- store < artifact;
- files fuera de bounds.

Resultado:

```text
exit 2
stderr = MCP_INVALID_HOST_OPTIONS
stdout empty
workspace unchanged
```

### Artifact ID/path attacks

Mediante authoring MCP:

- `../escape`;
- `/absolute`;
- `demo/draft`;
- backslash;
- colon/device-like;
- percent-encoded traversal;
- dot-leading invalid IDs.

Resultado: tool error seguro, ningún fichero fuera del workspace.

### Quota attacks

- artifact > individual limit;
- varios artefactos hasta superar global bytes;
- suficientes versiones para superar files;
- rejected write followed by allowed write with same expectedVersion;
- deduplicated content still requires a new manifest file and respects file quota.

## Integration project

```text
tests/BookStudio.Tests.McpSecuritySandbox
```

Journey:

```text
all five hosts reject unsafe roots/options
→ all five expose effective policy resource lazily
→ authoring rejects traversal IDs
→ authoring enforces individual quota
→ authoring fills bytes quota and rejects next write
→ rejected version is not consumed
→ file quota rejects new manifest
→ no outside files / no symlink traversal / no path leak
→ existing MCP conformance remains PASS
→ EOF
```

Symlink cases run when the platform permits creating symbolic links; Linux CI requires them and therefore they are mandatory in the release gate.

## Observability and errors

- Startup failure emits only `MCP_INVALID_HOST_OPTIONS`.
- Tool errors use stable codes and bounded safe messages.
- Server stdout remains JSON-RPC only.
- No quota scan emits filenames or paths.
- No policy includes the workspace root.

## CI

Contract:

```text
dotnet.mcp-security-sandbox-integration
```

Evidence:

```text
artifacts/ci/dotnet-mcp-security-sandbox-integration.json
```

The existing MCP conformance contract remains mandatory after the security journey.

## TDD Dual

### RED-I

Faltan policy, host options, quota exception, store enforcement, runtime wiring, resource decorator, integration, architecture and CI.

### RED-E

Current processes accept filesystem root and linked paths, and Artifact Store has no global bytes/files quota.

### GREEN-E

All five hosts reject unsafe policy; policy resource reflects effective limits; real authoring writes prove individual/global quotas and atomic version preservation.

## Definition of Done

- SPEC_READY;
- DUAL_RED_CONFIRMED;
- DUAL_GREEN;
- PATH_SANDBOX_PASS;
- FILE_QUOTA_PASS;
- POLICY_CONFORMANCE_PASS;
- MCP_CONFORMANCE_PASS;
- NO_ORPHANS_PASS;
- M_AUDIT_PASS;
- RETROSPEC_SYNCED.

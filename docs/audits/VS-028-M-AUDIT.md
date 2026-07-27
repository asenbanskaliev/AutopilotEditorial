# VS-028 — Auditoría M

## Resultado

`PASS`

## M1 — Specification

- Política sandbox común para los cinco servidores MCP bounded.
- Admisión fail-closed del workspace antes de iniciar JSON-RPC.
- Rechazo explícito de filesystem roots, existing files, symlinks, reparse points, UNC y device paths.
- Límites configurables y acotados para:
  - bytes por artefacto;
  - bytes permanentes del Artifact Store;
  - número de ficheros permanentes.
- Resource público, path-free y estable:
  - `book://security/sandbox-policy`.
- Enforcement de cuotas en el provider real, antes de publicar blob/manifest.
- Requisitos de limpieza temporal, no consumo de versión y recuperación tras rechazo.
- Journey externo sobre los cinco procesos y journey real de Artifact Store sin mocks.

## M2 — Implementation

- `McpWorkspaceSandboxPolicy` canonicaliza el root y valida cada componente existente del path.
- `McpHostOptions` incorpora parsing estricto de `--max-artifact-bytes`, `--max-store-bytes` y `--max-store-files`.
- Los defaults MCP son 16 MiB por artefacto, 1 GiB por store y 100000 ficheros.
- Los cinco composition roots envuelven su superficie con `SandboxEnabledFeatureRouter`.
- `McpSandboxPolicyResource` publica límites efectivos sin exponer paths físicos.
- Los runtimes core, authoring, quality y production reciben las opciones efectivas.
- `ArtifactStoreQuotaExceededException` mantiene el error provider-neutral.
- Authoring y Production convierten cuota agotada en errores de aplicación seguros.
- `FileArtifactStore`:
  - serializa quota-check y publicación mediante write gate;
  - mide únicamente blobs y manifests permanentes;
  - excluye temporales de consumo comprometido;
  - proyecta el delta exacto de blob y manifest;
  - reconoce blobs deduplicados;
  - valida cuota antes de mover contenido;
  - elimina un blob recién creado si falla la publicación del manifest;
  - conserva la versión requerida tras cualquier rechazo.

## M3 — Tests

Los contratos de Governance verifican:

- existencia de policy, resource, decorator, exception y proyecto de integración;
- flags y defaults de host;
- enforcement en los cinco programas y cuatro runtimes;
- registro en solución, arquitectura, catálogo CI y workflow;
- ausencia de nombres de paths físicos en la policy pública.

El journey subprocess verifica en los cinco servidores:

- rechazo de filesystem root;
- rechazo de existing file;
- rechazo de cuota de store inferior al límite individual;
- rechazo de números no canónicos;
- rechazo de symlink root cuando la plataforma permite crearlo;
- initialize MCP 2025-11-25;
- descubrimiento paginado completo de resources;
- policy presente exactamente una vez;
- lectura de policy y límites efectivos exactos;
- ausencia de workspace path en la respuesta;
- workspace perezoso durante lifecycle/discovery/policy read;
- EOF, exit 0, stdout agotado y stderr vacío.

El journey directo del provider real verifica:

- límite individual por artefacto;
- traversal de artifact ID;
- cuota proyectada de ficheros;
- cuota global de bytes;
- limpieza de temporales;
- no publicación de manifest/blob tras rechazo;
- no consumo de versión;
- deduplicación: un blob compartido y manifests independientes.

Todos los journeys acumulativos, prompts/resources y MCP conformance permanecen en PASS.

## M4 — Security and Operations

- El host falla antes de iniciar protocolo con exit code 2 y diagnóstico único `MCP_INVALID_HOST_OPTIONS`.
- Ningún error de options escribe en stdout.
- La policy pública no incluye workspace root ni nombres internos de base de datos.
- Los paths se canonicalizan y se comparan con semántica específica de plataforma.
- Los enlaces existentes se rechazan en host y dentro del Artifact Store.
- El store nunca contabiliza el fichero temporal como consumo comprometido.
- La cuota se evalúa bajo una exclusión mutua de proceso para evitar carreras internas.
- Las escrituras rechazadas se limpian y no avanzan la secuencia inmutable.
- Los límites son configurables, pero permanecen acotados por parsing y relación store >= artifact.

Riesgos residuales:

- La exclusión de cuota es intra-proceso; múltiples procesos escribiendo simultáneamente en el mismo workspace requerirán un lock interproceso futuro.
- La protección frente a symlink race depende de verificaciones before/after y de la semántica filesystem disponible; un sandbox con adversario local privilegiado requeriría aislamiento OS adicional.
- La medición recorre blobs/manifests y prioriza exactitud sobre rendimiento; un índice durable de uso será necesario a gran escala.
- El sandbox restringe filesystem del producto, no constituye aislamiento de proceso frente al sistema operativo completo.

## M5 — Product Flow

```text
parse bounded host options
→ canonicalize and admit workspace root
→ expose path-free effective policy
→ initialize MCP without activating workspace
→ execute bounded product operation
→ write and hash temp content
→ acquire write gate and artifact lock
→ validate immutable version
→ measure permanent store usage
→ project exact blob/manifest delta
→ reject cleanly or promote blob
→ publish manifest atomically
→ rollback newly promoted blob on publish failure
→ preserve evidence and EOF
```

## TestChangeRequest

### TCR-028-001

Aprobó extender los cinco catálogos acumulativos con:

```text
book://security/sandbox-policy
```

Los journeys recorren ahora resources hasta ausencia de `nextCursor`, conservando todos los schemas, profiles, prompts, orden, unicidad, invalid cursor, tools, lazy workspace, mutación y EOF previos.

No se eliminó ni relajó ninguna expectativa observable.

## Meta-Audit

- RED confirmado sobre head `e454d6e0e70cff83d2625361d93f7c4247d5e5be`:
  - Plan Integrity run `30263646504` PASS;
  - Governance run `30263646562` FAIL esperado por componentes ausentes.
- Primer GREEN funcional parcial en head `05fdb91d677c8f1ce72bf300dcceb7794b173fdc` detectó:
  - cinco expectations acumulativas desactualizadas por el resource autorizado;
  - cuota incorrecta al contar temporales y no proyectar blob+manifest exactos.
- El repair no rebajó pruebas:
  - corrigió la proyección permanente y rollback de blob;
  - registró `TCR-028-001`;
  - migró los cinco journeys a paginación completa.
- Un build posterior detectó una declaración mecánica ausente en book-core; se corrigió una sola línea sin alterar el contrato.
- No quedaron workflows ni scripts de migración temporales.
- No existen mocks de host, policy, procesos MCP o Artifact Store.
- No hay componentes huérfanos: solución, arquitectura, CI, runtimes y composición están enlazados.

## Evidencia GREEN

- Head funcional: `2526487c3bf83ea44744d3fa235e94363864eab7`.
- Plan Integrity: run `30269417984` PASS.
- Governance: run `30269418028` PASS.
- Governance artifact: `8654148879`.
- Governance digest: `sha256:aa39f1a20ff9e462dfe36dd5e2aaf750ea8911c0157318b8ecd95ae47389e55b`.
- .NET CI: run `30269418098`, job `89987962976` PASS.
- .NET artifact: `8654190966`.
- .NET digest: `sha256:989b3150c80227e9ba6db846f7ebeeb677d0868e90103b0feaf3d9790c1d4ade`.
- Security result: PASS, exit code 0, stderr vacío.
- Output:

```text
MCP_SECURITY_SANDBOX_PASS servers=5 invalidStarts=25 policyReads=5 quotaChecks=5
```

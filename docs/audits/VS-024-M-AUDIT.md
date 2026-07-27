# VS-024 — Auditoría M

## Resultado

`PASS`

## M1 — Specification

- Proceso acotado independiente: `BookStudio.Mcp.Production`.
- Surface activo exacto:
  - `book.release.prepare`;
  - `book.preflight.run`.
- Surface reservado y no anunciado:
  - `book.asset.register`;
  - `book.render.preview`;
  - `book.render.final`;
  - `book.publish.package`.
- Initialize anuncia únicamente tools/resources.
- `book.release.prepare` es write, non-destructive, non-idempotent, closed-world y `taskSupport = forbidden`.
- `book.preflight.run` es read-only, non-destructive, idempotent, closed-world y `taskSupport = forbidden`.
- La slice no promete renderizado, packaging ni publicación sin adapters reales.

## M2 — Implementation

- `BookStudio.Application.Production` contiene modelos, puerto y reglas de release/preflight.
- `ReleaseProductionService` depende de `IArtifactStore` y no referencia Infrastructure.
- `BookStudio.Mcp.Production` contiene composición, catálogo, schemas, profile resource y routing MCP.
- `BookProductionRuntime` crea store/servicio perezosamente.
- Release prepare verifica integridad de cada fuente antes de publicar el manifiesto.
- El manifiesto es JSON canónico, ordenado, acotado e inmutable.
- Preflight vuelve a verificar manifiesto y fuentes sin escribir artefactos ni estado.
- Los proyectos están registrados en solución, arquitectura y contrato CI.

## M3 — Tests

Los contratos estáticos verifican:

- archivos requeridos;
- surface activo/reservado;
- schemas y annotations diferenciadas;
- Application provider-neutral;
- proceso separado e identidad production;
- contrato CI y workflow.

El journey cruzado real verifica:

- authoring registra un manuscript y una fixture de cover incompatible;
- production initialize/list no crea workspace;
- identidad y capabilities exactas;
- tools/list exacto y sin render/package;
- resources/list paginado;
- lectura del perfil `release-basic`;
- release válida publicada de forma inmutable;
- preflight válido `PASS` con todos los checks pass;
- conflicto de versión estructurado;
- release con cover de media type incompatible;
- preflight incompatible `BLOCKED` por `release.role_media_compatibility`;
- rechazo de scope cruzado;
- rechazo de tool reservada;
- inventario idéntico antes/después de cada preflight;
- ausencia de paths, source content, stdout extra y stderr inseguro;
- EOF y exit 0.

Todos los journeys acumulativos permanecen en PASS.

## M4 — Security and Operations

- Manifiesto máximo: 1 MiB.
- Entre 1 y 50 fuentes.
- Exactamente un manuscript.
- Roles allow-listed.
- Scope de proyecto obligatorio.
- Fuentes duplicadas y autorreferencia rechazadas.
- Integrity-check obligatorio en prepare y preflight.
- Media type verificado según role.
- Sin egress, modelos, shell, render, publicación, overwrite o mutación de fuentes.
- Respuestas no contienen bytes de fuente, contenido completo, workspace o paths de store.
- El media type canónico `application/vnd.bookstudio.release-manifest+json` es lógico y no se confunde con un path físico.

Riesgos residuales:

- No hay renderer PDF/EPUB ni validadores específicos de formato en esta slice.
- `release-basic` comprueba disponibilidad, integridad y compatibilidad declarativa, no especificaciones completas de Amazon KDP.
- La release no representa todavía una aprobación editorial persistida.

## M5 — Product Flow

```text
book-authoring register immutable sources
→ launch bookstudio-production
→ book.release.prepare
→ verify source manifests and bytes
→ publish canonical immutable release manifest
→ book.preflight.run
→ verify release + every source
→ PASS or BLOCKED with stable reasons
→ no preflight mutation
→ EOF
```

## Meta-Audit

- RED interno y externo quedaron documentados antes de implementar el surface production.
- El primer intento integrado falló en compilación y se corrigió sin modificar contratos funcionales.
- Un GREEN posterior llegó al subprocess y detectó un falso positivo del harness: `.bookstudio` aparecía dentro del media type legítimo `vnd.bookstudio`, no como path.
- `TCR-024-001` aprobó acotar la detección a segmentos de ruta Linux y Windows JSON-escaped.
- No se cambió código production ni se redujo ninguna expectativa de inmutabilidad, integridad, scope, no-mutation o no-leak.
- El head `0f71bb4aa366c9cd4074dd55132f6f973f5d71e2` supera todos los gates.
- No existen handlers para tools reservadas ni componentes huérfanos dentro del alcance.

## Evidencia

- GREEN Plan Integrity: run `30249329561` PASS.
- GREEN Governance: run `30249329573` PASS.
- GREEN .NET: run `30249329567`, job `89923503650` PASS.
- Artifact: `8646323958`.
- Digest: `sha256:123f6c5882506b4f9b0521474bbe57f7eed141e87e9122ffadf2b2ac5b1487eb`.
- `dotnet.book-production-integration`: PASS.
- Exit code: 0.
- stdout: `BOOK_PRODUCTION_INTEGRATION_PASS`.
- stderr: empty.
- Build y architecture fitness: PASS.

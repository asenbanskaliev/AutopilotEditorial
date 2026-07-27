# VS-025 — Auditoría M

## Resultado

`PASS`

## M1 — Specification

- Proceso acotado independiente: `BookStudio.Mcp.Ops`.
- Surface activo exacto:
  - `book.ops.status`;
  - `book.ops.diagnostics`.
- Surface reservado y no anunciado:
  - `book.autopilot.start`;
  - `book.autopilot.status`;
  - `book.autopilot.pause`;
  - `book.autopilot.resume`;
  - `book.autopilot.cancel`;
  - `book.autopilot.replay`.
- Initialize anuncia únicamente tools/resources.
- Ambas tools son read-only, non-destructive, idempotent, closed-world y `taskSupport = forbidden`.
- El servidor comunica explícitamente `autopilotAvailability = unavailable`; no simula workflow, scheduler, worker, cancelación o replay antes de F4.

## M2 — Implementation

- `BookStudio.Application.Operations` contiene modelos, catálogo canónico, puerto y agregación de probes.
- `OperationsDiagnosticsService` depende de `IReadinessProbe` y no referencia Infrastructure ni MCP.
- `BookStudio.Mcp.Ops` contiene composición, schemas, catálogo, resources y routing MCP.
- `BookOpsRuntime` compone perezosamente:
  - `SqliteWorkspaceDatabase`;
  - `WorkspaceDatabaseReadinessProbe`;
  - `OperationsDiagnosticsService`.
- Status y diagnostics llaman exclusivamente a `CheckHealthAsync`; nunca llaman a `InitializeAsync`.
- El catálogo `book://ops/capabilities` se genera desde `OperationsCapabilityCatalog.All`, la misma fuente usada por diagnostics.
- Proyectos, solución, política arquitectónica, contrato CI y workflow están sincronizados.

## M3 — Tests

Los contratos estáticos verifican:

- archivos requeridos;
- surface activo/reservado;
- schemas y annotations read-only;
- Application provider-neutral;
- proceso separado e identidad ops;
- contrato CI y workflow.

El subprocess real verifica dos estados:

### Workspace ausente

- initialize con identidad `bookstudio-ops`;
- tools/list exacto y sin Autopilot;
- resources/list paginado y capability resource;
- status `notReady`, 0/1 probes ready y Autopilot unavailable;
- diagnostics con probe `workspace-database: missing`;
- recomendaciones estables para inicialización, F3 y F4;
- paridad exacta resource/diagnostics;
- el directorio sigue sin existir;
- argumentos adicionales rechazados;
- EOF limpio.

### Workspace ready

- SQLite se inicializa mediante `SqliteWorkspaceDatabase.InitializeAsync` real fuera del proceso ops;
- status `ready`, 1/1 probes ready;
- diagnostics reporta WAL/migrations de forma saneada mediante readiness;
- catálogo exacto: capacidades F1/F2 disponibles y F3/F4 reservadas;
- llamadas repetidas mantienen el inventario de archivos;
- `book.autopilot.start` se rechaza como tool desconocida/reservada;
- no se filtran workspace, `bookstudio.db`, conexión o stack trace;
- EOF, exit 0, stdout protocolario y stderr vacío.

Todos los journeys acumulativos permanecen en PASS.

## M4 — Security and Operations

- Sin egress, modelos, shell, workflows, mutación, repair o inicialización implícita.
- Los nombres/status de probes se sane­an y acotan.
- Excepciones de probes se convierten en `error` sin mensaje crudo.
- Respuestas no incluyen:
  - workspace root;
  - database path/file name;
  - connection string;
  - environment variables;
  - secretos;
  - stack traces.
- Inputs de tools son objetos exactamente vacíos.
- Resources son estáticos, acotados y paginados mediante cursores opacos.
- stdout se reserva para JSON-RPC; stderr solo admite códigos seguros.

Riesgos residuales:

- Solo existe un probe requerido: SQLite workspace database.
- El catálogo de disponibilidad describe capacidades de producto, no procesos vivos distribuidos.
- No hay aún métricas de scheduler, jobs, worker, OpenCode o workflows porque esos componentes no existen.

## M5 — Product Flow

```text
operator launches bookstudio-ops
→ initialize/list without workspace creation
→ status reads SQLite readiness
→ diagnostics returns sanitized checks + capability catalog
→ operator initializes workspace through Control Center/foundation
→ status becomes ready
→ repeated diagnostics remain read-only
→ Autopilot controls stay unavailable until F4
→ EOF
```

## Meta-Audit

- RED interno y externo quedaron documentados antes de implementar el proceso ops.
- El primer build pasó; architecture fitness detectó que el fixture SQLite compila también contra `WorkspaceDatabaseHealth` de Application.
- La política se corrigió para declarar esa referencia compilada permitida; no se cambió código funcional ni se debilitó ninguna prueba.
- El evidence runner del primer intento ya ejecutó el journey ops en PASS; el segundo run dejó además architecture fitness y todos los gates en PASS.
- No existe ningún handler para las seis tools Autopilot reservadas.
- No se detectan componentes huérfanos dentro del alcance.

## Evidencia

- GREEN Plan Integrity: run `30251603127` PASS.
- GREEN Governance: run `30251603089` PASS.
- GREEN .NET: run `30251603090`, job `89930660260` PASS.
- Branch head: `61c5681e0c7c2f6a045fc83fb5fa116df37762b9`.
- Normalized source SHA: `0dd01131733cb2289cd779d0e4772646fe753273`.
- Artifact: `8647212717`.
- Digest: `sha256:bfa9090e831ae27db46d0ea274023d6c38dccc44450567cb91b1ad09595b67e1`.
- `dotnet.book-ops-integration`: PASS.
- stdout: `BOOK_OPS_INTEGRATION_PASS`.
- exit code: 0.
- stderr: empty.
- Build y architecture fitness: PASS.

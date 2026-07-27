# VS-025 — book-ops Server

## IntentSpec

### Problema

BookStudio ya dispone de almacenamiento SQLite, health probes, observabilidad y cuatro servidores MCP funcionales, pero OpenCode, el workflow durable, scheduler y workers todavía pertenecen a fases posteriores. Un servidor ops no puede anunciar start/pause/cancel/replay como si esas capacidades existiesen.

### Objetivo

Crear un proceso MCP independiente `BookStudio.Mcp.Ops` con dos tools read-only y verificables:

1. `book.ops.status`;
2. `book.ops.diagnostics`.

Ambas usan la readiness real de SQLite y un catálogo explícito de capacidades implementadas/reservadas. No crean workflows, no reparan storage y no modifican el workspace.

## Surface público

### Tools activas

- `book.ops.status`
- `book.ops.diagnostics`

### Tools reservadas

- `book.autopilot.start`
- `book.autopilot.status`
- `book.autopilot.pause`
- `book.autopilot.resume`
- `book.autopilot.cancel`
- `book.autopilot.replay`

Las tools reservadas pueden existir como constantes para impedir deriva, pero no aparecen en `tools/list` ni tienen handlers.

## Servidor acotado

Ejecutable:

```text
src/BookStudio.Mcp.Ops/BookStudio.Mcp.Ops.csproj
```

Identidad:

```json
{
  "name": "bookstudio-ops",
  "title": "BookStudio Operations MCP"
}
```

Capabilities exactas:

```json
{
  "tools": { "listChanged": false },
  "resources": { "subscribe": false, "listChanged": false }
}
```

## Tool `book.ops.status`

Input:

```json
{}
```

Semántica:

- ejecuta todos los `IReadinessProbe` configurados, ordenados por nombre;
- no inicializa ni repara dependencias;
- devuelve `ready`, `notReady` o `degraded`;
- informa número total/ready de probes;
- informa `autopilotAvailability = unavailable` hasta que existan `AutopilotWorkflowRun + AutopilotJob`;
- informa que scheduler, worker y OpenCode no están disponibles todavía;
- nunca devuelve paths, conexión, variables de entorno o excepciones.

Annotations: read-only, non-destructive, idempotent, closed-world, `taskSupport = forbidden`.

## Tool `book.ops.diagnostics`

Input:

```json
{}
```

Semántica:

- ejecuta los mismos probes reales;
- devuelve checks saneados: name, ready, status, migration counts/version;
- devuelve un catálogo determinista de capacidades con estados `available` o `reserved`;
- devuelve recomendaciones estables y acotadas;
- no incluye archivos, paths, secretos, stack traces, uptime mutable ni contenido editorial;
- no inicializa, repara, cancela o reintenta nada.

Annotations: read-only, non-destructive, idempotent, closed-world, `taskSupport = forbidden`.

## Capabilities canónicas de esta slice

Disponibles:

- `foundation.sqlite`;
- `foundation.artifact-store`;
- `foundation.outbox`;
- `foundation.observability`;
- `mcp.book-core`;
- `mcp.book-authoring`;
- `mcp.book-quality`;
- `mcp.book-production`;
- `mcp.book-ops`.

Reservadas:

- `opencode.sessions`;
- `autopilot.workflow`;
- `autopilot.scheduler`;
- `autopilot.worker`;
- `autopilot.pause-resume-cancel`;
- `autopilot.replay`.

El catálogo refleja disponibilidad de producto, no disponibilidad transitoria de un proceso concreto.

## Resources

Schemas:

```text
book://schemas/book-ops/*
```

Catálogo estático:

```text
book://ops/capabilities
```

El catálogo resource y la respuesta diagnostics deben compartir los mismos IDs/estados.

## Application

Puerto:

```text
IOperationsDiagnosticsService
```

Métodos:

- `GetStatusAsync`;
- `RunDiagnosticsAsync`.

`OperationsDiagnosticsService` depende de `IReadinessProbe` y de un snapshot provider-neutral de capacidades. No referencia Infrastructure ni MCP.

## Runtime

`BookOpsRuntime` compone perezosamente:

- `SqliteWorkspaceDatabase`;
- `WorkspaceDatabaseReadinessProbe`;
- `OperationsDiagnosticsService`.

El constructor de runtime, initialize, tools/list y resources/list no crean directorios ni base de datos. `status` y `diagnostics` usan `CheckHealthAsync`, nunca `InitializeAsync`.

## Seguridad

- sin egress, modelos, shell, workflows, mutación o repair;
- errores esperados convertidos a códigos seguros;
- ninguna respuesta contiene workspace root, database path, connection string, environment variables o stack trace;
- stdout exclusivo para JSON-RPC;
- stderr limitado a códigos diagnósticos;
- argumentos adicionales rechazados.

## TDD Dual

### RED-I

Faltan Application service, proceso ops, schemas, catálogo, router, runtime, integración, arquitectura y CI.

### RED-E

No existe un proceso `BookStudio.Mcp.Ops` capaz de observar un workspace missing o ready mediante readiness SQLite real.

### GREEN-E

```text
missing workspace
→ initialize ops
→ list tools/resources
→ status notReady without creating workspace
→ diagnostics missing without leaks
→ initialize SQLite fixture through real infrastructure
→ restart ops
→ status ready
→ diagnostics ready + exact capability catalog
→ warm inventory snapshot
→ repeat diagnostics/status with no mutation
→ reserved autopilot rejection
→ EOF
```

## Auditoría M

- M1: surface activo/reservado e identidad exacta.
- M2: Application provider-neutral y composición lazy.
- M3: subprocess real, missing/ready, catalog parity y no mutation.
- M4: no paths/secrets/workflow simulation.
- M5: workspace state → ops status → diagnostics → operator next step.

## Definition of Done

- SPEC_READY;
- DUAL_RED_CONFIRMED;
- DUAL_GREEN;
- NO_ORPHANS_PASS;
- M_AUDIT_PASS;
- RETROSPEC_SYNCED.

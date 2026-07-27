# VS-025 — RetroSpec

## Implemented contract

BookStudio now contains a separate read-only operations MCP process:

```text
src/BookStudio.Mcp.Ops
```

Server identity:

```json
{
  "name": "bookstudio-ops",
  "title": "BookStudio Operations MCP"
}
```

Initialize advertises only tools and resources.

## Active tools

### `book.ops.status`

Runs all configured readiness probes in deterministic name order and returns:

- overall status: `ready`, `notReady` or `degraded`;
- total and ready probe counts;
- unready probe names;
- `autopilotAvailability = unavailable`;
- reserved component IDs.

The tool:

- accepts an exactly empty arguments object;
- calls readiness checks only;
- does not initialize or repair storage;
- is read-only, non-destructive, idempotent, closed-world and non-task.

### `book.ops.diagnostics`

Returns:

- the same overall readiness status;
- sanitized probe checks;
- applied/latest migration counts when available;
- the canonical product capability catalog;
- stable operator recommendations.

Recommendations currently cover:

- initializing a missing workspace through Control Center/foundation;
- completing F3 before model sessions;
- completing F4 before workflow controls;
- inspecting Control Center readiness for non-missing dependency failures.

The tool does not return uptime, paths, connection details, environment variables, secrets, stack traces or editorial content.

## Reserved tools

Unavailable and absent from tools/list:

- `book.autopilot.start`;
- `book.autopilot.status`;
- `book.autopilot.pause`;
- `book.autopilot.resume`;
- `book.autopilot.cancel`;
- `book.autopilot.replay`.

They require the later durable contracts:

```text
AutopilotWorkflowRun + AutopilotJob
```

plus scheduler, worker, operational controls and replay. No placeholder handlers or simulated state exist.

## Application contract

`IOperationsDiagnosticsService` exposes:

- `GetStatusAsync`;
- `RunDiagnosticsAsync`.

`OperationsDiagnosticsService`:

- depends on `IReadinessProbe` only;
- validates unique probe names;
- executes probes in deterministic order;
- catches unexpected probe failures and emits sanitized `error` checks;
- aggregates ready/notReady/degraded;
- returns stable recommendations;
- reads a provider-neutral capability snapshot;
- never references Infrastructure or MCP.

## Capability catalog

Available:

- `foundation.sqlite`;
- `foundation.artifact-store`;
- `foundation.outbox`;
- `foundation.observability`;
- `mcp.book-core`;
- `mcp.book-authoring`;
- `mcp.book-quality`;
- `mcp.book-production`;
- `mcp.book-ops`.

Reserved:

- `opencode.sessions`;
- `autopilot.workflow`;
- `autopilot.scheduler`;
- `autopilot.worker`;
- `autopilot.pause-resume-cancel`;
- `autopilot.replay`.

The catalog is defined once in `OperationsCapabilityCatalog.All`. Both diagnostics and the resource below are generated from that same list:

```text
book://ops/capabilities
```

## Resources

Static resources:

```text
book://ops/capabilities
book://schemas/book-ops/empty-input
book://schemas/book-ops/status-output
book://schemas/book-ops/diagnostics-output
book://schemas/book-ops/tool-result
```

Resources are bounded, sorted and paginated with scope/fingerprint-bound opaque cursors.

## Runtime contract

`BookOpsRuntime` lazily composes:

- `SqliteWorkspaceDatabase`;
- `WorkspaceDatabaseReadinessProbe`;
- `OperationsDiagnosticsService`.

The runtime constructor, initialize, tools/list and resources/list do not create a workspace. First tool execution creates only in-memory objects; readiness invokes `CheckHealthAsync`, which returns `missing` when the database does not exist.

`InitializeAsync` is never called from book-ops.

## Missing-workspace behavior

```text
status = notReady
probeCount = 1
readyProbeCount = 0
unreadyProbes = [workspace-database]
autopilotAvailability = unavailable
```

Diagnostics reports the database probe as `missing`, recommends foundation initialization, and leaves the workspace directory absent.

## Ready-workspace behavior

After real SQLite initialization through Infrastructure:

```text
status = ready
probeCount = 1
readyProbeCount = 1
workspace-database = ready
```

Diagnostics reports migration counts/version through the sanitized readiness contract. Repeated status/diagnostics calls leave the warmed workspace file inventory unchanged.

## CI contract

```text
dotnet.book-ops-integration
```

Journey:

```text
missing workspace
→ initialize/list/resources
→ status notReady without creation
→ diagnostics missing + recommendations
→ real SQLite InitializeAsync fixture
→ new ops process
→ status ready
→ diagnostics ready + capability parity
→ repeat without mutation
→ reserved Autopilot rejection
→ EOF
```

Normalized GREEN evidence:

```text
BOOK_OPS_INTEGRATION_PASS
exitCode = 0
stderr = empty
```

## Architecture

New projects:

- `BookStudio.Mcp.Ops`: protocol adapter referencing Application, Infrastructure and shared MCP protocol.
- `BookStudio.Tests.BookOps`: integration executable referencing Infrastructure and the ops process project.

The test assembly also contains a compiled reference to Application because the real SQLite lifecycle returns `WorkspaceDatabaseHealth`. This reference is explicitly allowed by architecture policy; no additional ProjectReference was introduced.

Both projects are registered in solution, architecture policy, CI catalog, workflow and scoped AGENTS instructions.

## Deviations and fixes

- Autopilot controls remain reserved because their durable state does not exist yet.
- The initial architecture declaration omitted the test assembly's transitive compiled Application contract. Architecture fitness caught it and the declaration was corrected.
- No production code or functional acceptance test was changed during the repair.

## Follow-on constraints

- Autopilot tools may become active only after F4 durable workflow/job state exists.
- Start/pause/resume/cancel/replay require authorization, concurrency and idempotency contracts before exposure.
- New ops probes must use `IReadinessProbe`, sanitize failures and remain read-only unless a separate repair tool is specified.
- Capability IDs/statuses must remain synchronized through the canonical Application catalog.
- Diagnostics must never expose physical configuration or raw exceptions.

## Next slice

`VS-026 — Prompts and resources`.

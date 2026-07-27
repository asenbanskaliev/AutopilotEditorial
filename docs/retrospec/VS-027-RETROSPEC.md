# VS-027 — RetroSpec

## Implemented contract

BookStudio now has one transversal MCP 2025-11-25 conformance executable:

```text
tests/BookStudio.Tests.McpConformance
```

It launches the five real bounded servers through `dotnet` and newline-delimited stdio. No router, session, runtime or Application service is called directly.

## Target matrix

| Assembly | Expected server name |
|---|---|
| `BookStudio.Mcp.dll` | `bookstudio` |
| `BookStudio.Mcp.Authoring.dll` | `bookstudio-authoring` |
| `BookStudio.Mcp.Quality.dll` | `bookstudio-quality` |
| `BookStudio.Mcp.Production.dll` | `bookstudio-production` |
| `BookStudio.Mcp.Ops.dll` | `bookstudio-ops` |

All servers must advertise exactly prompts, resources and tools with immutable list capabilities.

## Versioned corpus

```text
Corpus/mcp-conformance-v1.json
```

Metadata:

- schemaVersion `1.0.0`;
- protocolVersion `2025-11-25`;
- 27 cases;
- phases `created` and `ready`;
- unique stable case IDs.

Covered categories:

- empty/whitespace input;
- truncated JSON;
- trailing comma;
- comments;
- non-object roots;
- missing/wrong jsonrpc;
- missing, non-string, empty or controlled method;
- invalid ID kinds;
- non-object params;
- feature before initialize;
- invalid initialize;
- unknown ready method;
- duplicate initialize;
- initialized notification with ID;
- invalid prompts cursor.

Corpus v1 is immutable. Incompatible semantic changes require v2.

## Deterministic generated cases

Configuration:

```text
seed = 27027
cases per server = 128
servers = 5
total = 640
```

Categories are selected reproducibly and have deterministic expected outcomes:

- jsonrpc missing, wrong or non-string;
- method missing, non-string, empty or longer than 128;
- invalid boolean, object or array IDs;
- array, string, number or null params.

A valid ping is required every 16 generated cases. The ordered assembly/payload stream is hashed with SHA-256.

Verified stream digest:

```text
2af65427878c95b3d582413703247f46828debca5d694f0a456ef0a65b61d4b2
```

## Lifecycle journey

For every server:

```text
pre-initialize ping
→ created corpus
→ JSON depth overflow
→ initialize notification ignored
→ recovery ping
→ initialize request
→ duplicate initialize rejection
→ initialized notification
→ duplicate initialized notification ignored
→ tools/resources/prompts discovery
→ ready corpus
→ reused request ID rejection
→ unknown notification with canary
→ recovery ping
→ 128 generated cases with survival pings
→ >1 MiB message rejection
→ final ping
→ EOF
```

## Process driver

`McpProcessDriver` provides:

- real child process startup;
- redirected UTF-8 stdin/stdout/stderr;
- raw message and typed request/notification writes;
- 10-second response timeout;
- JSON-only line reads;
- 20-second EOF/exit timeout;
- forced termination only during test cleanup after timeout.

No failed case is retried.

## Response contract

Errors require:

- `jsonrpc = 2.0`;
- expected integer code;
- null or exact readable string ID;
- non-empty message;
- no result member.

Success responses require:

- `jsonrpc = 2.0`;
- exact string ID;
- result present;
- error absent.

## Recovery contract

The process must remain operational after:

- malformed JSON;
- invalid JSON-RPC shape;
- invalid lifecycle messages;
- unknown notification;
- each block of 16 generated cases;
- message larger than 1 MiB.

Recovery is proven by a subsequent valid ping on the same process.

## Security contract

Forbidden in server stderr:

- secret canary;
- workspace root;
- `.bookstudio`;
- `bookstudio.db`;
- connection-string markers.

Every stderr line must be an alphanumeric/underscore/hyphen diagnostic of at most 96 characters.

The test requires the workspace path to remain nonexistent because it invokes only lifecycle and discovery methods.

## Final report

Success produces exactly one line:

```text
MCP_CONFORMANCE_PASS servers=5 corpus=27 fuzz=640 seed=27027 sha256=2af65427878c95b3d582413703247f46828debca5d694f0a456ef0a65b61d4b2
```

The test process exits 0 and has empty stderr.

## CI contract

```text
dotnet.mcp-conformance-integration
```

The workflow runs conformance after prompts/resources and creates:

```text
artifacts/ci/dotnet-mcp-conformance-integration.json
```

## TestChangeRequest

`TCR-027-001` documents that the runner owns execution/report data while `Program` owns the console marker and exit code. External behavior is unchanged.

## Deviations

- This is deterministic category-based generation, not coverage-guided native fuzzing.
- The suite targets the current stdio transport only.
- The suite does not execute product tools with side effects.
- Numeric request-ID preservation is already covered by MCP initialize journeys; generated cases use stable string IDs for reproducible reporting.

## Follow-on constraints

- New bounded MCP servers must be added to this matrix before release.
- New transports require separate conformance drivers.
- Protocol upgrades require a new corpus version or explicit compatibility layer.
- Security sandbox rules from VS-028 must extend, not replace, this suite.
- No future optimization may bypass real subprocess execution.

## Next slice

`VS-028 — MCP security sandbox`.

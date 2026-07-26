# VS-021 — RetroSpec

## Implemented contract

`BookStudio.Mcp` now exposes the first executable `book-core` surface backed by real Application use cases and the durable Artifact Store.

Active tools:

1. `book.artifact.get`;
2. `book.artifact.compare`.

Reserved but unavailable tools:

- `book.project.create`;
- `book.project.get_status`;
- `book.project.configure`;
- `book.decision.submit`.

Reserved names are not returned by `tools/list` and calls to them fail as unknown tools.

## Initialize capabilities

The server now advertises exactly:

```json
{
  "tools": { "listChanged": false },
  "resources": { "subscribe": false, "listChanged": false }
}
```

No other optional MCP capability is advertised.

## Tool contract

Both tools are:

- read-only;
- non-destructive;
- idempotent;
- closed-world;
- non-task operations.

Every tool definition contains:

- stable name, title and description;
- inputSchema;
- outputSchema;
- annotations;
- `execution.taskSupport = forbidden`.

### `book.artifact.get`

Input requires:

- projectId;
- payload.artifactId;
- payload.version;
- optional payload.includeContent.

Behavior:

- validates project and artifact scope;
- reads and integrity-checks an immutable artifact version;
- returns metadata and a logical resource URI;
- optionally includes UTF-8 text up to 256 KiB;
- returns warnings instead of pretending binary or oversized content is inline;
- never returns a physical path.

### `book.artifact.compare`

Input requires:

- projectId;
- payload.artifactId;
- payload.leftVersion;
- payload.rightVersion;
- optional payload.maxDifferences.

Behavior:

- loads and verifies both immutable versions;
- compares hashes first;
- performs deterministic bounded LCS line diff only for compatible text;
- returns added/removed counts and bounded structured operations;
- falls back to metadata comparison for binary or oversized content.

## Application contract

`IArtifactQueryService` is provider-neutral and implemented by `ArtifactQueryService`.

Responsibilities:

- validate project scope;
- read verified artifacts through `IArtifactStore`;
- construct logical resource URIs;
- control safe inline content;
- produce bounded deterministic comparison results;
- map expected store failures to stable query error codes.

Application does not reference filesystem implementation types and does not expose physical paths.

## Resource contract

Static schema resources are available under:

```text
book://schemas/book-core/*
```

The immutable artifact resource template is:

```text
book://project/{projectId}/artifact/{artifactId}/versions/{version}
```

`resources/read` returns:

- text for compatible UTF-8 content up to 1 MiB;
- base64 blob for binary content up to 1 MiB;
- a stable `resource_too_large` protocol error above the limit.

## Listing and cursor contract

- Active tools are ordered ordinally by name.
- Static resources are ordered ordinally by URI.
- List methods support opaque cursors.
- Cursor payload includes version, list scope, offset and catalog fingerprint.
- Cursor checksum uses SHA-256 and fixed-time comparison.
- Invalid, stale, modified or cross-list cursors return Invalid params.

## Composition contract

Workspace root precedence:

1. `--workspace-root`;
2. `BOOKSTUDIO_WORKSPACE_ROOT`;
3. platform-local default.

The path is canonicalized, but the directory and Artifact Store are created lazily only on the first artifact operation.

Initialize, ping, tools/list, resources/list and resources/templates/list do not create the workspace.

## Structured results

Tool execution success returns `CallToolResult` with:

- `isError = false`;
- concise content;
- structuredContent matching outputSchema;
- deterministic operation ID;
- immutable artifact references;
- warnings;
- optional resource link.

Expected domain failures return:

- `isError = true`;
- stable error code and safe message in structuredContent.

Malformed MCP params, unknown tool names and invalid resources remain JSON-RPC errors.

## Architecture contract

- `BookStudio.Application.Artifacts`: query models and use case.
- `BookStudio.Mcp.BookCore`: schemas, catalog, cursors and MCP router.
- `BookStudio.Mcp.Protocol`: lifecycle and JSON-RPC dispatch.
- `BookStudio.Infrastructure.Artifacts.FileSystem`: durable store adapter.
- `Program.cs`: host options, lazy composition and stdio lifecycle.

## CI contract

Contract ID:

```text
dotnet.book-core-integration
```

The independent executable launches the real MCP child process and verifies:

```text
lazy initialize/list
→ seed immutable artifacts
→ initialize
→ tools/list
→ resources/list pagination
→ invalid cursor
→ templates/list
→ schema read
→ artifact.get text
→ text resource read
→ artifact.compare
→ project-scope failure
→ reserved-tool rejection
→ artifact.get binary
→ binary resource read
→ oversize resource rejection
→ unknown resource rejection
→ EOF
```

The cumulative `dotnet.mcp-initialize-integration` contract remains green with tools/resources capabilities.

## Deviations and fixes

- The original plan named six canonical book-core tools, but only two had real supporting use cases. The implementation activates only those two and reserves the remaining names without stubs.
- The first build failed because `padded.Length % 4 switch` was parsed as `%` against a string switch result. The expression was corrected to `(padded.Length % 4) switch`.
- No tests were weakened to obtain GREEN.

## Follow-on constraints

- Project and decision tools may only become active after their durable Application use cases exist.
- Future book-core additions must update initialize capabilities, schemas, conformance and subprocess tests together.
- No tool may expose filesystem paths, secrets or raw exception messages.
- Write operations require their own authorization, idempotency and destructive annotations.
- Resource subscriptions and listChanged notifications remain unsupported until a dedicated slice implements them.

## Next slice

`VS-022 — book-authoring server`.

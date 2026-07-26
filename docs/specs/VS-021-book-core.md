# VS-021 — book-core Tools and Resources

## IntentSpec

### Problem

VS-020 establishes a correct MCP lifecycle but advertises no capabilities. The platform needs its first bounded executable MCP surface with complete schemas, deterministic listing, structured results and real Application use cases. Project and decision workflows do not yet exist and must not be represented by stubs.

### Objective

Expose the artifact-reading subset of the canonical `book-core` server:

- `book.artifact.get`;
- `book.artifact.compare`;

and a read-only resource surface for schemas and immutable artifact versions. Preserve the remaining canonical book-core names as reserved, non-advertised identifiers until real project/workflow use cases are implemented.

## Scope decision

### Active tools

1. `book.artifact.get`
2. `book.artifact.compare`

Both are backed by `IArtifactStore` through an Application query service.

### Reserved, unavailable and not advertised

- `book.project.create`;
- `book.project.get_status`;
- `book.project.configure`;
- `book.decision.submit`.

They may exist as constants in the reserved catalog to prevent naming drift. They must not appear in `tools/list`, must not be listed as capabilities and `tools/call` must treat them as unknown until a later canonical slice registers an executable handler.

No response may claim `accepted`, create fake operation IDs or simulate a durable workflow for an unavailable tool.

## MCP capabilities

After this slice, initialize advertises exactly:

```json
{
  "tools": { "listChanged": false },
  "resources": { "subscribe": false, "listChanged": false }
}
```

It does not advertise prompts, logging, completions, elicitation, sampling, roots, tasks or experimental features.

Changing the VS-020 integration assertion from empty capabilities to this exact surface requires an approved TestChangeRequest. All lifecycle assertions remain unchanged.

## Tool definitions

Names follow the MCP tool-name constraint and are stable lowercase dotted identifiers.

Every definition includes:

- `name`;
- `title`;
- `description`;
- `inputSchema`;
- `outputSchema`;
- `annotations`;
- `execution.taskSupport = forbidden`.

Annotations for both tools:

```json
{
  "readOnlyHint": true,
  "destructiveHint": false,
  "idempotentHint": true,
  "openWorldHint": false
}
```

### Common project scope

`projectId` is required and must match:

```text
^[a-z0-9][a-z0-9-]{0,63}$
```

An artifact belongs to the project only when its artifact ID begins with:

```text
{projectId}.
```

This enforces project confinement even before a durable project registry exists.

### book.artifact.get

Input:

```json
{
  "projectId": "project-slug",
  "payload": {
    "artifactId": "project-slug.chapter-01",
    "version": 1,
    "includeContent": false
  }
}
```

- `artifactId` uses the Artifact Store lowercase slug contract.
- `version >= 1`.
- `includeContent` defaults false.
- Additional properties are rejected.

Result data includes:

- immutable artifact reference;
- project-scoped resource URI;
- optional inline UTF-8 text;
- content inclusion state.

Inline text is allowed only when:

- media type is text-compatible;
- content length is at most 256 KiB;
- UTF-8 is valid.

Otherwise, the tool remains successful, returns the immutable reference and adds a warning. It never returns a physical path.

### book.artifact.compare

Input:

```json
{
  "projectId": "project-slug",
  "payload": {
    "artifactId": "project-slug.chapter-01",
    "leftVersion": 1,
    "rightVersion": 2,
    "maxDifferences": 20
  }
}
```

- versions must be positive and distinct;
- `maxDifferences` is 1–100, default 20;
- additional properties are rejected.

Comparison behavior:

1. Load and integrity-check both manifests.
2. Equal SHA-256 means identical without content diff.
3. Text diff is attempted only when both media types are compatible, total content is at most 1 MiB and each side has at most 500 lines.
4. Use deterministic LCS-based line operations.
5. Return added/removed counts and up to `maxDifferences` structured operations.
6. For binary/oversize/too-many-lines content, return metadata comparison plus a warning; do not pretend a line diff exists.

## Common structured result

`structuredContent` matches the advertised `outputSchema`:

```json
{
  "resultType": "complete | failed",
  "operationId": "stable bounded identifier",
  "artifactRefs": [],
  "warnings": [],
  "data": {},
  "error": null
}
```

- Read operations use deterministic operation IDs derived from input and immutable hashes.
- Domain/tool execution errors return `CallToolResult` with `isError: true`.
- Tool-not-found and malformed MCP method params remain JSON-RPC errors.
- Content contains a concise text summary and, where applicable, a `resource_link` item.
- Raw exception messages, filesystem paths and payloads are never returned.

## Application service

Application adds a provider-neutral `IArtifactQueryService` backed by `IArtifactStore`.

Responsibilities:

- validate project and artifact scope;
- obtain manifests and verified streams;
- build logical resource URIs;
- decide whether content can be safely inlined;
- perform bounded deterministic text diff;
- map expected artifact-store failures to stable query errors.

It does not expose filesystem paths and does not depend on Infrastructure types.

## Resources

### Static schema resources

- `book://schemas/book-core/tool-result`;
- `book://schemas/book-core/artifact-get-input`;
- `book://schemas/book-core/artifact-get-output`;
- `book://schemas/book-core/artifact-compare-input`;
- `book://schemas/book-core/artifact-compare-output`.

Each uses `application/schema+json` and returns canonical compact JSON.

### Resource template

```text
book://project/{projectId}/artifact/{artifactId}/versions/{version}
```

`resources/templates/list` advertises this template.

### Artifact resource read

`resources/read` parses the URI, validates project scope and reads an immutable version with integrity verification.

- Text-compatible content up to 1 MiB returns `text`.
- Binary content up to 1 MiB returns `blob` as base64.
- Larger content returns a stable protocol error and remains accessible through the tool reference only.
- The response includes logical URI and MIME type, never a path.

## Listing and cursors

- Tools are ordered by name ordinal.
- Static resources are ordered by URI ordinal.
- List methods accept an optional opaque cursor.
- Cursor format is versioned, checksum-protected and scoped to the catalog type.
- Invalid, stale or cross-list cursors return `Invalid params`.
- Page boundaries are stable for an immutable catalog.

## Composition

`BookStudio.Mcp` accepts:

```text
--workspace-root <absolute-or-relative-path>
```

or environment variable:

```text
BOOKSTUDIO_WORKSPACE_ROOT
```

Precedence: argument, environment, platform-local default.

- The path is canonicalized.
- The artifact store is created lazily on first tool/resource operation.
- Initialize, ping and list operations do not create workspace directories.
- The router/store is disposed on process shutdown.

## TDD Dual

### RED-I

Missing:

- async feature router;
- tool/resource models and schemas;
- catalog and cursors;
- Application artifact query service;
- artifact-backed handlers;
- MCP host options;
- CI contract and contract tests.

### RED-E

The subprocess advertises `{}` capabilities and returns method-not-found for every tools/resources method.

### GREEN-I

Schemas, catalog, Application service, architecture and governance pass.

### GREEN-E

A real subprocess with a disposable workspace proves:

- initialize advertises only tools/resources;
- lifecycle regressions remain green;
- deterministic tools/resources listing;
- schemas and annotations;
- resource template;
- artifact get metadata and inline text;
- artifact resource read;
- structured text comparison;
- binary artifact reference and blob resource;
- project-scope rejection;
- invalid input as tool error;
- unknown tool/resource as protocol error;
- no physical paths or secrets;
- clean EOF and protocol-only stdout.

## Audit M

- M1: exact active/reserved surface, schemas and semantics.
- M2: Application use case vs MCP adapter vs Infrastructure composition.
- M3: subprocess, durable store, schemas, cursors and adversarial cases.
- M4: project confinement, bounded content/diff, no paths, no stubs.
- M5: initialize → list → call/read → compare → EOF.

## Definition of Done

- SPEC_READY.
- DUAL_RED_CONFIRMED.
- DUAL_GREEN.
- NO_ORPHANS_PASS.
- M_AUDIT_PASS.
- RETROSPEC_SYNCED.

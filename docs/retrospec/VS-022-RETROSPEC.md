# VS-022 — RetroSpec

## Implemented contract

BookStudio now contains a separate bounded MCP process for deterministic draft authoring:

```text
src/BookStudio.Mcp.Authoring
```

Server identity:

```json
{
  "name": "bookstudio-authoring",
  "title": "BookStudio Authoring MCP"
}
```

Initialize advertises only tools and resources.

## Active tools

### `book.draft.register`

Publishes one immutable text draft version.

Input:

- projectId;
- artifactId scoped as `{projectId}.draft.*`;
- expectedVersion;
- text/markdown or text/plain;
- non-empty Unicode content.

Limits and behavior:

- maximum 512 KiB UTF-8;
- control characters except CR/LF/tab rejected;
- exact sequential version required;
- existing versions are never overwritten;
- returns logical reference, hash, length, media type and resource URI;
- version conflict is a structured tool error.

Annotations:

- readOnlyHint false;
- destructiveHint false;
- idempotentHint false;
- openWorldHint false;
- taskSupport forbidden.

### `book.draft.validate`

Integrity-checks and deterministically validates a stored textual draft.

Returns:

- characters;
- words;
- lines;
- paragraphs;
- Markdown heading count;
- bounded warning categories;
- isValid.

Warnings cover:

- empty content;
- long lines;
- trailing whitespace;
- tabs;
- NUL;
- unsupported controls.

Annotations:

- readOnlyHint true;
- destructiveHint false;
- idempotentHint true;
- openWorldHint false;
- taskSupport forbidden.

## Reserved tools

The following identifiers are reserved but unavailable:

- `book.plan.create`;
- `book.scene.generate`;
- `book.chapter.generate`;
- `book.manuscript.assemble`.

They are absent from tools/list and unknown to tools/call. No stub, fake model output or placeholder handler exists.

## Application contract

`IDraftAuthoringService` exposes:

- RegisterAsync;
- ValidateAsync;
- ReadResourceAsync.

`DraftAuthoringService`:

- depends only on `IArtifactStore`;
- validates project/artifact scope;
- applies encoding and size limits;
- maps expected store failures to stable safe codes;
- produces deterministic metrics and warnings;
- never exposes physical paths.

## Protocol contract

`McpSession` now accepts an optional bounded `McpImplementationInfo` for per-process identity. Existing callers without identity preserve:

```text
bookstudio / BookStudio MCP
```

The authoring process supplies its own identity while reusing the verified lifecycle and stdio transport.

`BookAuthoringFeatureRouter` implements:

- tools/list;
- tools/call;
- resources/list;
- resources/templates/list;
- resources/read.

Tool errors use CallToolResult with `isError = true`. Malformed params, reserved tools and unknown resources remain JSON-RPC Invalid params errors.

## Resources

Static schemas:

```text
book://schemas/book-authoring/*
```

Draft template:

```text
book://project/{projectId}/artifact/{artifactId}/versions/{version}
```

Draft resource reads:

- require project-confined draft artifacts;
- verify integrity;
- require supported textual media type and valid UTF-8;
- return text only;
- reject content above 1 MiB;
- never return a blob or physical path.

## Runtime contract

Workspace precedence is inherited from McpHostOptions:

1. `--workspace-root`;
2. `BOOKSTUDIO_WORKSPACE_ROOT`;
3. platform-local default.

`BookAuthoringRuntime` creates FileArtifactStore and DraftAuthoringService on first data operation. Initialize and list methods do not create the workspace.

## CI contract

```text
dotnet.book-authoring-integration
```

The external journey launches the real authoring child process and verifies:

```text
lazy initialize/list
→ initialize identity
→ tools/list
→ resources pagination/schema
→ register v1
→ validate v1
→ read v1
→ conflict
→ scope error
→ invalid controls
→ register v2
→ warning validation
→ reserved-tool error
→ EOF
```

## Architecture

New projects:

- `BookStudio.Mcp.Authoring`: protocol-adapter referencing Application, Infrastructure and MCP protocol assembly.
- `BookStudio.Tests.BookAuthoring`: integration executable referencing only the authoring process project.

Both are registered in the solution, architecture policy and scoped AGENTS instructions.

## Deviations and fixes

- Generation, planning and assembly are not implemented because OpenCode and durable authoring workflows do not yet exist. Their names remain reserved.
- The initial scoped AGENTS files lacked mandatory Allowed/Forbidden headings. Governance caught the defect; both files were corrected without changing behavior tests.
- No functional acceptance criterion was weakened.

## Follow-on constraints

- Future generation tools require real OpenCode and workflow use cases.
- Any active write tool must preserve immutable version semantics and explicit non-idempotent annotation.
- New authoring resources must remain textual, bounded and path-free unless a dedicated contract expands this.
- Model-generated content must not be introduced directly into this protocol adapter.

## Next slice

`VS-023 — book-quality server`.

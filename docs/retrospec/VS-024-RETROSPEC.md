# VS-024 — RetroSpec

## Implemented contract

BookStudio now contains a separate deterministic production MCP process:

```text
src/BookStudio.Mcp.Production
```

Server identity:

```json
{
  "name": "bookstudio-production",
  "title": "BookStudio Production MCP"
}
```

Initialize advertises only tools and resources.

## Active tools

### `book.release.prepare`

Verifies project-scoped immutable source artifacts and publishes one canonical release manifest.

Input:

- projectId;
- releaseId;
- expectedVersion;
- title;
- language;
- 1..50 source references.

Rules:

- output artifact ID is `{projectId}.release.{releaseId}`;
- exactly one source has role `manuscript`;
- roles are allow-listed;
- every source belongs to the project;
- every source version exists and passes integrity verification;
- duplicate references and release self-reference are rejected;
- sources are sorted deterministically;
- existing release versions are never overwritten.

Output media type:

```text
application/vnd.bookstudio.release-manifest+json
```

The tool is write, non-destructive, non-idempotent, closed-world and non-task.

### `book.preflight.run`

Reads one immutable release manifest, verifies it and re-verifies every referenced source.

Profile:

```text
release-basic
```

Checks:

- `release.schema_version`;
- `release.project_scope`;
- `release.manuscript_present`;
- `release.no_duplicate_sources`;
- `release.sources_available`;
- `release.sources_integrity`;
- `release.role_media_compatibility`.

Decision:

- `PASS` when all checks pass;
- `BLOCKED` with stable blocking reasons otherwise.

The tool is read-only, non-destructive, idempotent, closed-world and non-task. It does not persist approval state or mutate the release.

## Source roles and media compatibility

- manuscript: text/markdown, text/plain;
- cover: image/png, image/jpeg, image/svg+xml;
- metadata: application/json;
- interior-pdf: application/pdf;
- epub: application/epub+zip;
- supplemental: any non-empty media type.

## Reserved tools

Unavailable and absent from tools/list:

- `book.asset.register`;
- `book.render.preview`;
- `book.render.final`;
- `book.publish.package`.

No placeholder renderer, fake package or simulated publication handler exists.

## Application contract

`IReleaseProductionService` exposes:

- `PrepareAsync`;
- `RunPreflightAsync`.

`ReleaseProductionService`:

- depends only on `IArtifactStore`;
- validates project/release/source scope;
- verifies all source bytes and manifests;
- serializes a bounded canonical JSON document;
- publishes immutable versions;
- re-verifies release and sources during preflight;
- maps expected failures to stable safe codes;
- never exposes source content or physical paths.

## Resources

Profile:

```text
book://production/profiles/release-basic
```

Schemas:

```text
book://schemas/book-production/*
```

Resources are static, bounded and paginated through opaque scope/fingerprint-bound cursors.

## Runtime contract

`BookProductionRuntime` lazily composes:

- `FileArtifactStore`;
- `ReleaseProductionService`.

Initialize and list methods do not create the workspace.

## Cross-server product flow

The integration journey:

1. launches `BookStudio.Mcp.Authoring`;
2. registers a manuscript and an intentionally incompatible cover fixture;
3. launches `BookStudio.Mcp.Production` on the shared workspace;
4. prepares a valid immutable release;
5. runs a passing preflight;
6. verifies immutable version conflict;
7. prepares a release with incompatible role/media assignment;
8. runs a blocked preflight;
9. proves each preflight leaves the workspace inventory unchanged;
10. rejects scope crossing and reserved render tools;
11. closes through EOF with clean stdout/stderr.

## CI contract

```text
dotnet.book-production-integration
```

Normalized GREEN evidence:

```text
BOOK_PRODUCTION_INTEGRATION_PASS
exitCode = 0
stderr = empty
```

## Architecture

New projects:

- `BookStudio.Mcp.Production`: protocol adapter referencing Application, Infrastructure and shared MCP protocol.
- `BookStudio.Tests.BookProduction`: subprocess integration referencing authoring and production process projects.

Both are registered in solution, architecture policy, CI catalog, workflow and scoped AGENTS instructions.

## TestChangeRequest

`TCR-024-001` corrected a false-positive path assertion. The broad substring `.bookstudio` collided with the canonical vendor media type `vnd.bookstudio`. The test now rejects path-specific Linux and JSON-escaped Windows `.bookstudio` segments while retaining the full workspace-root, source-content, stdout and stderr leak checks.

No production behavior or security expectation was weakened.

## Deviations

- Rendering, packaging and publication remain reserved because no real adapters exist yet.
- `release-basic` does not claim complete KDP format compliance.
- Release approval and publication state are not persisted in this slice.

## Follow-on constraints

- Future render tools require actual deterministic render adapters, versioned toolchains and golden-file evidence.
- Package/publish tools require explicit authorization, release locks, retry semantics and provider-specific adapters.
- KDP preflight profiles must extend rather than replace `release-basic` source integrity checks.
- Production adapters must never mutate source artifacts or overwrite release versions.

## Next slice

`VS-025 — book-ops server`.

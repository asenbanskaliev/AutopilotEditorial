# VS-023 — RetroSpec

## Implemented contract

BookStudio now contains a separate deterministic quality MCP process:

```text
src/BookStudio.Mcp.Quality
```

Server identity:

```json
{
  "name": "bookstudio-quality",
  "title": "BookStudio Quality MCP"
}
```

Initialize advertises only tools and resources.

## Active tools

### `book.audit.run`

Reads and integrity-checks one immutable project-scoped draft, then returns bounded deterministic metrics and checks.

Metrics:

- characters;
- words;
- lines;
- paragraphs;
- Markdown headings;
- sentences;
- placeholders;
- adjacent duplicate paragraphs;
- long sentences.

Checks:

- `content.non_empty`;
- `content.minimum_words`;
- `content.no_placeholders`;
- `content.no_adjacent_duplicate_paragraphs`;
- `style.maximum_sentence_words`;
- `structure.has_paragraphs`.

The tool is read-only, non-destructive, idempotent, closed-world and non-task.

### `book.gate.evaluate`

Runs the deterministic audit and evaluates profile `draft-basic`.

Returns:

- decision `PASS` or `BLOCKED`;
- stable blocking reasons;
- embedded audit metrics/checks;
- no persisted approval or lock change.

Settings:

- minimumWords 1..50000;
- maximumWarnings 0..100;
- blockOnPlaceholders;
- profile must be `draft-basic`.

The tool is read-only, non-destructive, idempotent, closed-world and non-task.

## Reserved tools

Unavailable and absent from tools/list:

- `book.repair.propose`;
- `book.repair.apply`;
- `book.memory.get`;
- `book.memory.commit`.

No placeholder handlers or fake model output exist.

## Application contract

`IQualityAssessmentService` exposes:

- `RunAuditAsync`;
- `EvaluateGateAsync`.

`QualityAssessmentService`:

- depends only on `IArtifactStore`;
- validates `{projectId}.draft.*` scope;
- verifies integrity;
- accepts only text/markdown and text/plain;
- decodes strict UTF-8;
- limits reads to 2 MiB;
- applies deterministic regex/line/paragraph/sentence rules;
- maps expected failures to stable safe codes;
- never returns physical paths or full source text.

## Profile resource

```text
book://quality/profiles/draft-basic
```

The JSON document lists the six checks and default thresholds.

Schemas are available under:

```text
book://schemas/book-quality/*
```

Resources are paginated through opaque scope/fingerprint-bound cursors.

## Runtime contract

`BookQualityRuntime` lazily composes:

- `FileArtifactStore`;
- `QualityAssessmentService`.

Initialize and list methods do not create the workspace.

## Read-only proof

The integration test:

1. launches `BookStudio.Mcp.Authoring`;
2. registers clean and failing immutable drafts;
3. records all workspace files;
4. launches `BookStudio.Mcp.Quality`;
5. runs audits/gates/resources;
6. records workspace files again;
7. requires identical inventories.

Quality does not publish artifacts, mutate versions, write memory or persist gate decisions.

## CI contract

```text
dotnet.book-quality-integration
```

Journey:

```text
lazy quality initialize/list
→ authoring register clean/failing drafts
→ quality identity/tools/profile
→ clean audit PASS checks
→ clean gate PASS
→ failing audit placeholder/duplicate/long-sentence findings
→ failing gate BLOCKED
→ scope rejection
→ reserved repair rejection
→ no mutation
→ EOF
```

## Architecture

New projects:

- `BookStudio.Mcp.Quality`: protocol adapter referencing Application, Infrastructure and shared MCP protocol.
- `BookStudio.Tests.BookQuality`: subprocess integration referencing authoring and quality process projects.

Both are registered in solution, architecture policy and scoped AGENTS instructions.

## Deviations

- Repair and memory tools remain reserved because no safe workflows or durable use cases exist.
- Sentence splitting is heuristic and deterministic, not model-based linguistic analysis.
- Gate decisions are returned but not persisted.
- No functional test was weakened; the first complete implementation passed build and journey without code repair.

## Follow-on constraints

- Future repair tools require explicit proposal/apply separation, authorization, immutable patches and human gates.
- Memory tools require canonical memory contracts and transactional commit semantics.
- New quality profiles must be versioned resources and have independent tests.
- Model-based quality must not replace deterministic checks or run inside the protocol adapter.

## Next slice

`VS-024 — book-production server`.

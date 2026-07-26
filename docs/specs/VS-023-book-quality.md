# VS-023 — book-quality Server

## IntentSpec

### Problema

Los drafts ya pueden registrarse y validarse estructuralmente, pero no existe un servidor MCP acotado que produzca auditorías deterministas y decisiones de gate reutilizables. Tampoco existen todavía OpenCode ni workflows capaces de proponer o aplicar reparaciones narrativas de forma segura.

### Objetivo

Crear un proceso MCP independiente `BookStudio.Mcp.Quality` con dos tools reales:

1. `book.audit.run`;
2. `book.gate.evaluate`.

Ambas leen versiones inmutables del Artifact Store, no modifican contenido y no invocan modelos.

## Surface público

### Tools activas

- `book.audit.run`
- `book.gate.evaluate`

### Tools reservadas

- `book.repair.propose`
- `book.repair.apply`
- `book.memory.get`
- `book.memory.commit`

Las tools reservadas no se anuncian ni tienen handlers.

## Servidor acotado

Ejecutable:

```text
src/BookStudio.Mcp.Quality/BookStudio.Mcp.Quality.csproj
```

Identidad:

```json
{
  "name": "bookstudio-quality",
  "title": "BookStudio Quality MCP"
}
```

Capabilities exactas: tools y resources.

## Tool `book.audit.run`

Input:

```json
{
  "projectId": "demo",
  "payload": {
    "artifactId": "demo.draft.chapter-01",
    "version": 1,
    "minimumWords": 100,
    "maximumSentenceWords": 60
  }
}
```

Semántica:

- valida scope `{projectId}.draft.*`;
- verifica integridad, media type textual y UTF-8;
- lectura máxima 2 MiB;
- calcula caracteres, palabras, líneas, párrafos, headings, frases, placeholders, párrafos adyacentes duplicados y frases largas;
- produce checks deterministas con estados pass/warn/fail;
- no escribe artefactos ni modifica memoria.

Annotations: read-only, non-destructive, idempotent, closed-world, taskSupport forbidden.

## Tool `book.gate.evaluate`

Input:

```json
{
  "projectId": "demo",
  "payload": {
    "artifactId": "demo.draft.chapter-01",
    "version": 1,
    "profile": "draft-basic",
    "minimumWords": 100,
    "maximumWarnings": 3,
    "blockOnPlaceholders": true
  }
}
```

Semántica:

- ejecuta la auditoría determinista;
- evalúa el perfil `draft-basic`;
- devuelve decisión `PASS` o `BLOCKED`;
- razones estables y checks que bloquearon;
- no persiste una aprobación ni cambia locks: esas capacidades pertenecen a workflows posteriores.

Annotations: read-only, non-destructive, idempotent, closed-world, taskSupport forbidden.

## Checks mínimos

- `content.non_empty`;
- `content.minimum_words`;
- `content.no_placeholders`;
- `content.no_adjacent_duplicate_paragraphs`;
- `style.maximum_sentence_words`;
- `structure.has_paragraphs`.

Placeholders reconocidos como tokens completos, case-insensitive:

- TODO;
- TBD;
- FIXME;
- XXX.

## Resources

Schemas:

```text
book://schemas/book-quality/*
```

Perfil:

```text
book://quality/profiles/draft-basic
```

No se expone el texto del draft; el cliente usa book-core/authoring para leer contenido.

## Application

Puerto:

```text
IQualityAssessmentService
```

Métodos:

- `RunAuditAsync(QualityAuditQuery)`;
- `EvaluateGateAsync(QualityGateQuery)`.

Application depende de IArtifactStore y no referencia Infrastructure.

## Runtime

`BookQualityRuntime` crea perezosamente store y servicio.

Initialize, tools/list y resources/list no crean el workspace.

## Seguridad

- sin egress, modelos, prompts, shell o mutación;
- lectura acotada e integrity-check;
- scope de proyecto obligatorio;
- sin paths físicos, texto completo ni excepción cruda en resultados;
- límites: minimumWords 1..50000, maximumSentenceWords 10..300, maximumWarnings 0..100.

## TDD Dual

### RED-I

Faltan Application service, proceso quality, schemas, catálogo, router, runtime, integración, arquitectura y CI.

### RED-E

No existe un proceso quality capaz de auditar o decidir un gate sobre un draft producido por book-authoring.

### GREEN-E

```text
book-authoring register clean + failing drafts
→ quality lazy initialize/list
→ audit clean
→ gate clean PASS
→ audit failing
→ gate failing BLOCKED
→ profile resource
→ scope rejection
→ reserved repair rejection
→ EOF
```

## Definition of Done

- SPEC_READY;
- DUAL_RED_CONFIRMED;
- DUAL_GREEN;
- NO_ORPHANS_PASS;
- M_AUDIT_PASS;
- RETROSPEC_SYNCED.

# VS-013 — Artifact Store

## IntentSpec

### Problem

Future chapters, briefs, audits, images and renders need a durable canonical file protocol. Direct agent-supplied paths would permit overwrite, traversal, loss of history and undetected corruption.

### Objective

Implement a provider-neutral artifact port and a filesystem implementation that stores immutable content-addressed blobs plus immutable, versioned manifests.

### Boundaries

- No editorial artifact taxonomy yet.
- No UI, MCP tools or SQLite artifact index yet.
- No mutable blobs or in-place manifest edits.
- No caller-controlled absolute paths.

## BehaviorSpec

### Identity

- Artifact IDs are lowercase slugs: `^[a-z0-9][a-z0-9._-]{0,127}$`.
- A version is a positive integer.
- Content is identified by lowercase SHA-256.
- Each `(artifactId, version)` manifest is immutable.

### Layout

```text
.bookstudio/artifacts/
  blobs/sha256/aa/<remaining-hash>
  manifests/<artifact-id>/<version>.json
  temp/<random>.tmp
```

### Put journey

1. Validate artifact ID, media type, expected version and size limit.
2. Reject reparse points/symlinks in every existing path segment.
3. Stream to a workspace-confined temporary file while hashing.
4. Flush and close the temp file.
5. Promote the blob atomically with create-new semantics; concurrent identical content deduplicates.
6. Create the immutable manifest atomically with create-new semantics.
7. On manifest conflict, return a version conflict without replacing the existing manifest.
8. Remove temporary files on success, cancellation or failure.

### Read journey

- Load and validate the manifest.
- Resolve only the canonical blob path derived from the hash.
- Reject missing, malformed, symlinked or mismatched content.
- Stream with optional verification; full verification is mandatory in the integration journey.

### Listing

- List manifests for an artifact in ascending version order.
- Never infer versions from blob names.

### Quotas

- Per-artifact maximum content length is configurable and enforced while streaming.
- Empty streams are allowed.
- Caller cancellation aborts without publishing a manifest.

### Errors

- invalid artifact ID;
- invalid media type;
- non-positive expected version;
- size exceeded;
- path escape or symlink/reparse point;
- version conflict;
- missing blob or manifest;
- malformed manifest;
- content hash or length mismatch;
- disposed store.

## TDD Dual

### RED-I

Static governance tests require the Application contracts, filesystem implementation, manifest schema/layout documentation and integration coverage before they exist.

### RED-E

No executable currently proves ingest, dedupe, version conflict, concurrency, cancellation, tamper detection and cleanup.

### GREEN-I

Static contracts, solution architecture and build pass.

### GREEN-E

The real integration executable proves the full store journey on a clean GitHub runner and emits normalized evidence.

## Audit M

- M1: contracts match issue and spec.
- M2: immutable implementation with one canonical layout.
- M3: positive, negative, concurrent and tamper tests.
- M4: confinement, symlink protection, quotas, atomicity and cleanup.
- M5: end-to-end stream → manifest → verified read journey.

## Definition of Done

- SPEC_READY.
- DUAL_RED_CONFIRMED.
- DUAL_GREEN.
- NO_ORPHANS_PASS.
- M_AUDIT_PASS.
- RETROSPEC_SYNCED.

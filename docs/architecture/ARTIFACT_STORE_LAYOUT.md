# Artifact Store Layout

The canonical store is rooted at `.bookstudio/artifacts` inside a workspace.

```text
.bookstudio/artifacts/
├── blobs/sha256/aa/<remaining-hash>
├── manifests/<artifact-id>/<version>.json
└── temp/<random>.tmp
```

## Invariants

- Blobs are immutable and content-addressed by lowercase SHA-256.
- Manifests are immutable and versioned at `manifests/<artifact-id>/<version>.json`.
- A manifest is the canonical mapping from logical identity to a blob.
- Publication uses temporary files and atomic create-new promotion.
- Existing blobs may be deduplicated only after their hash and length are verified.
- Existing manifests are never overwritten.
- Artifact IDs, hashes and versions determine every path; callers never supply filesystem paths.
- All paths remain inside the workspace and existing symlinks/reparse points are rejected.
- Cancellation or failure removes temporary files. A promoted blob without a manifest is an allowable recoverable orphan and may later be deduplicated.
- Reads validate manifest identity and may perform full content verification; verification is mandatory at trust boundaries.

## Concurrency

A process-local per-artifact lock serializes version allocation. Filesystem create-new semantics provide the final cross-process conflict boundary. Two concurrent writers for the same expected version produce one manifest and one explicit version conflict.

## Recovery

Manifests are self-contained JSON records. A future SQLite index may accelerate queries, but it must be rebuildable from manifests and cannot become the source of truth for blob identity.

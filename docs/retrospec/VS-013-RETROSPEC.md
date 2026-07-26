# VS-013 — RetroSpec

## Implemented contract

The product now has a provider-neutral immutable artifact port and a filesystem provider rooted at `.bookstudio/artifacts`.

### Canonical identity

- logical identity: artifact ID + positive version;
- physical identity: lowercase SHA-256 + byte length;
- publication metadata: immutable JSON manifest schema `1.0.0`.

### Durable layout

```text
blobs/sha256/<prefix>/<hash-tail>
manifests/<artifact-id>/<version>.json
temp/<random>.tmp
```

### Guarantees

- streaming size enforcement;
- content hashing during write;
- flush-to-disk before promotion;
- atomic create-new blob and manifest promotion;
- verified deduplication;
- sequential immutable versions;
- explicit concurrent version conflicts;
- workspace confinement;
- symlink/reparse rejection;
- cancellation and failure cleanup;
- malformed manifest, missing blob and content tamper detection;
- verified reads and independent integrity checks.

## Deviations from initial implementation approach

An asynchronous module initializer was initially used to attach the new journey to the existing integration executable. GitHub CI showed that this could block assembly startup while awaiting asynchronous continuations. It was removed and replaced by `BookStudio.Tests.Artifacts`, an explicit independently runnable executable. The behavior contract did not change and no test was removed.

## Operational contract

- CI executes SQLite and artifact journeys as separate processes.
- Normalized evidence uses `dotnet.artifact-store-integration`.
- SQLite is not the source of truth for artifact identity.
- Future indexes must be rebuildable from manifests.

## Follow-on constraints

- Project, chapter and audit entities may reference artifact IDs and versions, never arbitrary paths.
- MCP tools must accept artifact handles or workspace-relative staging files and must not expose blob paths as writable targets.
- Garbage collection must treat manifest-referenced blobs as live and orphan blobs as recoverable candidates.
- Future encryption or remote storage providers must preserve the same immutable manifest semantics.

## Next slice

`VS-014 — Outbox and domain events`.

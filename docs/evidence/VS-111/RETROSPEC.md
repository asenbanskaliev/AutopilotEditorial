# VS-111 RetroSpec

## What changed from RED to GREEN

The repository moved from having no governed EPUB renderer to an exact-authority, deterministic and durable render lifecycle with provider-neutral contracts, canonical XHTML/navigation/OPF construction, package-entry persistence, validation findings, replay receipts, optimistic concurrency, append-only history and deterministic Outbox effects.

## Specification confirmations

- Rendering consumes one exact approved VS-110 manuscript revision.
- Package structure, paths, entry ordering and digests are deterministic.
- `mimetype` is first and uncompressed.
- Resources retain rights approval, safe paths, media types and content digests.
- Figures require accessibility alternatives.
- Blocking structural, accessibility or EPUBCheck-compatible findings prevent approval.
- Failed, stale or conflicting operations fail closed.

## Correction discovered during implementation

The first persistence implementation stored a `Draft` state with `Package = null` even though the orchestrator could build a deterministic package. This made approval unreachable. The final implementation now materializes the package from the exact authority before submission and atomically persists the package plus every ordered entry as a `Rendered` state.

## Residual risks and controls

External EPUBCheck execution is represented as governed validation findings rather than an embedded process in this slice. The risk is contained by blocking findings, exact evidence digests, immutable package identity and later technical preflight/certification slices.

## Final acceptance rule

The exact final SHA must independently pass Plan Integrity, Governance Gates and .NET CI before ready-for-review or merge.

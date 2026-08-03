# VS-134 — KDP Production Package

## Goal

Produce a deterministic publication package from approved chapter artifacts without manual file assembly.

## Delivered

- EPUB 3 container with mimetype, container.xml, OPF, navigation and chapter XHTML.
- Deterministic print-interior PDF using a built-in font.
- Metadata, discoverability fields, ISBN placeholder and KDP checklist.
- Cover-input validation for dimensions and 300 DPI minimum.
- Trim and margin validation.
- Stable manifest containing relative paths, lengths and SHA-256 hashes.
- Reproducible final ZIP with stable entry order and timestamps.
- Fail-closed behavior for invalid manuscript, trim, margins, cover or metadata.

## Acceptance proof

The harness builds the same package twice and requires identical package and manifest hashes, verifies the EPUB/PDF/metadata/checklist/cover/manifest contents, and proves a 72-DPI cover is blocked.

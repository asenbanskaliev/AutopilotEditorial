# VS-021 — Dual Red Evidence

## RED-I

`Governance Gates` run `30218175031` failed in the book-core contract tests after Plan Integrity completed successfully.

Missing behavior:

- no Application artifact query/compare service;
- no async MCP feature router;
- no active/reserved book-core catalog;
- no input/output schemas or annotations;
- no cursor codec;
- no tools/resources handlers;
- no workspace-root composition;
- no subprocess journey or CI contract.

## RED-E

The merged VS-020 process advertises an empty capability object and returns method-not-found for `tools/list`, `tools/call`, `resources/list`, `resources/templates/list` and `resources/read`.

## Confirmation

Existing Artifact Store tests prove durable immutable storage but do not prove an MCP tool/resource surface. Existing MCP initialize tests prove only lifecycle and protocol framing. No previous PASS is reused as VS-021 evidence.

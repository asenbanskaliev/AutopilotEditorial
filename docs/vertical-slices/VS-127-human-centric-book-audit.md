# VS-127 — Full human-centric book creation audit

## SDD intent
Prove through one executable integration journey that a non-technical user can provide a natural-language book idea and reach a verified EPUB, PDF, DOCX and KDP package without technical commands.

## Acceptance
- durable checkpoint and exact artifact SHA-256 evidence;
- licensed image provenance, territory and alt text;
- total provider cost remains within the declared book ceiling;
- automatic repair ceiling is explicit and finite;
- restart returns the same terminal revision, cost and bytes;
- missing mandatory rights evidence rejects publication fail-closed;
- audit evidence is written atomically inside the workspace;
- no second VS, branch or PR;
- Do not merge until all required checks pass on the same final SHA.

## Dual TDD
- Product-side integration smoke executes the real provider-backed proof authority.
- Governance-side Python test proves that the smoke remains connected to the CI entrypoint and covers the required invariants.

## M Audit
The audit covers integration, persistence, restart, costs, bounded repair, rights, accessibility and exact evidence. It does not claim a completed external human usability study or live paid-provider benchmark.

## Meta-Audit
The evidence must distinguish executable automation proof from human-panel proof. Passing this slice proves the automated user journey and its failure boundaries, not subjective literary preference across audiences.

## RetroSpec
Earlier slices proved individual authorities. VS-127 binds them into one user-centered audit and records the remaining external-human and live-provider validation gaps explicitly.

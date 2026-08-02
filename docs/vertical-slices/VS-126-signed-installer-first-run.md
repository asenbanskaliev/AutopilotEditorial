# VS-126 — Signed installer and guided first-run setup

## Intent
Close the installation gap with a deployment-grade Windows path that verifies the exact package and signer, resumes safely after interruption, guides provider and budget configuration, protects credentials, records exact evidence and launches BookStudio without technical commands.

## Invariants
- Package SHA-256 and Authenticode signature must both validate before extraction.
- All state, evidence and credentials remain confined to the selected installation root.
- First-run progress is persisted atomically after each phase and resumes without repeating completed phases.
- Provider credential material is protected with Windows DPAPI for the current user and never written in plaintext.
- A non-negative monthly cost ceiling is mandatory before readiness.
- Automated repair attempts are bounded; exceeding the ceiling fails closed for manual review.
- Completed installations are idempotent and do not repeat setup.
- Exact installation evidence includes digest, signer, signature status, installation root, provider, cost ceiling, credential mechanism and repair count.

## Acceptance
The executable installer and governance tests prove digest/signature rejection, path confinement, atomic durable state, restart resume, protected credentials, cost disclosure, bounded repair and no-command launch.

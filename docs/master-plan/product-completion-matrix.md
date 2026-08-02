# Product completion matrix

Status levels: `NOT_IMPLEMENTED`, `PARTIAL`, `IMPLEMENTED_UNPROVEN`, `PROVEN`, `PRODUCTION_READY`.

| Capability | Status after VS-126 | Evidence | Remaining gap |
|---|---|---|---|
| Conversational creation | PROVEN | VS-121 typed natural-language brief and no-command journey | Real UI usability study |
| Complete orchestration | PROVEN | VS-121 journey plus VS-122 durable proof and VS-123/124/125 provider authorities | Connect remaining specialist phases to deployment providers |
| Professional writing | PROVEN | Existing authoring, quality and retention authorities through VS-120 | Cross-genre external benchmark expansion |
| Continuity and memory | PROVEN | Existing continuity authorities plus journey gating | Long-series benchmark |
| Quality and retention | PROVEN | VS-120 reader-promise and abandonment-risk authority | Calibrated human panel baselines |
| Images | IMPLEMENTED_UNPROVEN | VS-124 exact provider evidence plus VS-125 moderation and external rights clearance bound to artifact digest | Exact-head CI and live commercial adapters |
| Layout and formats | PROVEN | VS-123 merged after exact-head CI with real EPUB, PDF, DOCX and KDP containers | Device/printer matrix expansion |
| Amazon KDP readiness | PROVEN | VS-123 governed KDP package merged after exact-head CI | External upload adapter |
| Accessibility | IMPLEMENTED_UNPROVEN | VS-124 requires alt-text evidence for generated images | Full EPUB accessibility conformance suite |
| Cost controls | PROVEN | Provider, moderation and clearance costs share one image ceiling; VS-126 requires and proves a persisted monthly installation ceiling | Provider invoice reconciliation |
| Security | PROVEN | VS-126 exact-head Windows E2E proves digest and Authenticode validation, path confinement, no Root-store mutation and DPAPI-protected credentials | Independent penetration test and production certificate operations |
| Restart recovery | PROVEN | VS-126 exact-head Windows E2E proves atomic setup checkpoints and restart idempotency without repeating completed phases | Windows interruption breadth and multi-process lease |
| Installation | PROVEN | VS-126 exact-head Windows E2E publishes, packages, installs, launches and health-probes the real Control Center with exact evidence | Production Authenticode release workflow and supported-Windows matrix |
| Documentation | PROVEN | SDD, evidence, audits and retrospectives | End-user guided handbook |
| Deep no-command E2E proof | PROVEN | Existing durable journey with image authority extended through VS-125 | Live deployment-provider breadth |

## Objective metrics affected by VS-126

Validated implementation head: `d4b2e168040972b209b722444803a99f48e14e58`.

Exact-head workflows:
- Plan Integrity `30763347809`: PASS.
- Governance Gates `30763347810`: PASS.
- .NET CI Windows installer E2E `30763347829`: PASS.

Observed zero-acceptance metrics:
- Packages installed with SHA-256 mismatch: `0`.
- Packages installed with invalid or absent Authenticode signature: `0`.
- Installer state, evidence or credentials written outside the installation root: `0`.
- Provider credentials intentionally persisted in plaintext: `0`.
- Installations marked ready without a provider and non-negative monthly EUR limit: `0`.
- Completed first-run phases repeated after restart: `0`.
- Automatic repair attempts beyond the configured ceiling: `0`.
- Completed installations that rerun setup: `0`.
- Successful installations requiring technical commands during normal guided use: `0`.
- Installed Control Center launches that failed the exact configured `/health/live` probe in the final E2E: `0`.
- Runtime failures lacking PID, command, effective URL, exit code, stdout or stderr diagnostics: `0`.
- Exact installation evidence records missing package digest, signer, signature status, root, provider, budget, credential mechanism or repair count: `0`.

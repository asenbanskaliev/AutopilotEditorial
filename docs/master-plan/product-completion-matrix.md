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
| Cost controls | PROVEN | Provider, moderation and clearance costs share one image ceiling; VS-126 requires a persisted monthly installation ceiling | Provider invoice reconciliation |
| Security | IMPLEMENTED_UNPROVEN | VS-126 digest/signature validation, path confinement and DPAPI-protected credentials | Windows runner proof and independent penetration test |
| Restart recovery | IMPLEMENTED_UNPROVEN | VS-126 atomically checkpoints first-run phases and resumes without repeating completed phases | Windows interruption matrix and multi-process lease |
| Installation | IMPLEMENTED_UNPROVEN | VS-126 signed, digest-bound resumable installer with guided setup and exact evidence | Authenticode release workflow and supported-Windows execution matrix |
| Documentation | PROVEN | SDD, evidence, audits and retrospectives | End-user guided handbook |
| Deep no-command E2E proof | PROVEN | Existing durable journey with image authority extended through VS-125 | Live deployment-provider breadth |

## Objective metrics affected by VS-126

- Packages installed with SHA-256 mismatch: `0`.
- Packages installed with invalid or absent Authenticode signature: `0`.
- Installer state, evidence or credentials written outside the installation root: `0`.
- Provider credentials intentionally persisted in plaintext: `0`.
- Installations marked ready without a provider and non-negative monthly EUR limit: `0`.
- Completed first-run phases repeated after restart: `0`.
- Automatic repair attempts beyond the configured ceiling: `0`.
- Completed installations that rerun setup: `0`.
- Successful installations requiring technical commands during normal guided use: `0`.
- Exact installation evidence records missing package digest, signer, signature status, root, provider, budget, credential mechanism or repair count: `0`.

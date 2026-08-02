# Product completion matrix

Status levels: `NOT_IMPLEMENTED`, `PARTIAL`, `IMPLEMENTED_UNPROVEN`, `PROVEN`, `PRODUCTION_READY`.

| Capability | Status after VS-123 | Evidence | Remaining gap |
|---|---|---|---|
| Conversational creation | PROVEN | VS-121 typed natural-language brief and no-command journey | Real UI usability study |
| Complete orchestration | PROVEN | VS-121 journey plus VS-122 durable proof and VS-123 provider boundary | Connect every specialist phase to deployment providers |
| Professional writing | PROVEN | Existing authoring, quality and retention authorities through VS-120 | Cross-genre external benchmark expansion |
| Continuity and memory | PROVEN | Existing continuity authorities plus journey gating | Long-series benchmark |
| Quality and retention | PROVEN | VS-120 reader-promise and abandonment-risk authority | Calibrated human panel baselines |
| Images | IMPLEMENTED_UNPROVEN | Existing visual production authorities | Real image provider and rights provenance E2E |
| Layout and formats | IMPLEMENTED_UNPROVEN | VS-123 creates real EPUB, PDF, DOCX and KDP containers with exact evidence | Same-head CI and device/printer matrix |
| Amazon KDP readiness | IMPLEMENTED_UNPROVEN | VS-123 creates a governed KDP package without claiming upload | Same-head CI and external upload adapter |
| Accessibility | IMPLEMENTED_UNPROVEN | Existing production checks | Full EPUB accessibility conformance suite |
| Cost controls | PROVEN | VS-121/122 policy plus VS-123 provider quote/result ceilings | Provider invoice reconciliation |
| Security | PROVEN | Workspace confinement, atomic writes, digest verification and fail-closed gates | Independent penetration test |
| Restart recovery | PROVEN | VS-122 checkpoints plus VS-123 deterministic artifact replay | Multi-process distributed lease |
| Installation | PARTIAL | Repository build and CI workflows | Signed installer and first-run setup |
| Documentation | PROVEN | SDD, evidence, audits and retrospectives | End-user guided handbook |
| Deep no-command E2E proof | PROVEN | VS-122 merged with exact-head CI | Deployment provider breadth |

## Objective metrics affected by VS-123

- Real publication formats emitted: `4/4` (EPUB, PDF, DOCX, KDP).
- Required container structures checked in executable smoke: `3/3`.
- PDF signature checked: `PASS` in executable smoke, pending same-head CI.
- Technical commands required during normal production: `0`.
- Provider costs beyond configured ceiling accepted: `0`.
- Unsupported required formats silently omitted: `0`.
- Workspace path escapes accepted: `0`.
- Artifacts without SHA-256/media type/provenance accepted: `0`.
- Byte drift after deterministic restart replay: `0`.

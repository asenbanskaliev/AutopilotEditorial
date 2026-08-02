# Product completion matrix

Status levels: `NOT_IMPLEMENTED`, `PARTIAL`, `IMPLEMENTED_UNPROVEN`, `PROVEN`, `PRODUCTION_READY`.

| Capability | Status after VS-124 | Evidence | Remaining gap |
|---|---|---|---|
| Conversational creation | PROVEN | VS-121 typed natural-language brief and no-command journey | Real UI usability study |
| Complete orchestration | PROVEN | VS-121 journey plus VS-122 durable proof and VS-123/124 provider boundaries | Connect remaining specialist phases to deployment providers |
| Professional writing | PROVEN | Existing authoring, quality and retention authorities through VS-120 | Cross-genre external benchmark expansion |
| Continuity and memory | PROVEN | Existing continuity authorities plus journey gating | Long-series benchmark |
| Quality and retention | PROVEN | VS-120 reader-promise and abandonment-risk authority | Calibrated human panel baselines |
| Images | IMPLEMENTED_UNPROVEN | VS-124 provider-backed SVG bytes with exact provenance, rights, accessibility, cost and restart evidence | Same-head CI, commercial adapter and moderation service |
| Layout and formats | PROVEN | VS-123 merged after exact-head CI with real EPUB, PDF, DOCX and KDP containers | Device/printer matrix expansion |
| Amazon KDP readiness | PROVEN | VS-123 governed KDP package merged after exact-head CI | External upload adapter |
| Accessibility | IMPLEMENTED_UNPROVEN | VS-124 requires alt-text evidence for generated images | Full EPUB accessibility conformance suite |
| Cost controls | PROVEN | VS-121/122 policy plus VS-123 publication and VS-124 image quote/result ceilings | Provider invoice reconciliation |
| Security | PROVEN | Workspace confinement, atomic writes, digest verification and fail-closed gates | Independent penetration test |
| Restart recovery | PROVEN | VS-122 checkpoints, VS-123 artifact replay and VS-124 verified image reuse | Multi-process distributed lease |
| Installation | PARTIAL | Repository build and CI workflows | Signed installer and first-run setup |
| Documentation | PROVEN | SDD, evidence, audits and retrospectives | End-user guided handbook |
| Deep no-command E2E proof | PROVEN | VS-122 merged with exact-head CI and provider boundaries extended through VS-124 | Deployment provider breadth |

## Objective metrics affected by VS-124

- Real image formats emitted: `1/1` reference SVG provider.
- Images without exact SHA-256/media type/provider/model/request evidence accepted: `0`.
- Images without allowed license, rights holder, reference and sufficient territory accepted: `0`.
- Images without alt text accepted: `0`.
- Provider costs beyond configured ceiling accepted: `0`.
- Automatic repair calls beyond configured ceiling: `0`.
- Workspace path escapes accepted: `0`.
- Byte drift after deterministic restart replay: `0`.
- Technical commands required during normal image production: `0`.

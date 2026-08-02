# VS-123 — RetroSpec

Status: IMPLEMENTED — pending same-head CI.

VS-123 replaces the simulated artifact boundary with a provider-backed pipeline and a deterministic zero-cost reference provider. It creates actual EPUB, PDF, DOCX and KDP package bytes, keeps outputs inside the workspace, writes atomically, records exact provenance and rejects cost or format policy violations.

Retained boundaries:
- provider adapters produce files but do not bypass editorial approval;
- external Amazon upload is not claimed;
- commercial providers can be added behind the same contract;
- restart reuse is accepted only when exact bytes still match.

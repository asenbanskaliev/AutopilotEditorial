# VS-061 RED Evidence

VS-060 provides approved generated scenes, but the repository has no durable paragraph-level coherence audit.

Missing behavior at slice start:

- no deterministic paragraph segmentation and exact ranges;
- no versioned local-coherence findings;
- no finding decisions or blocking-close rule;
- no causal binding to the approved scene digest;
- no idempotent close event;
- no restart and workspace-isolation journey.

Contracts and migration exist, but VS-061 remains RED until the real SQLite store and cumulative journey pass all gates.
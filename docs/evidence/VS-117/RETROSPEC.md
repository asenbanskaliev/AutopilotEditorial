# VS-117 RetroSpec

## What the implementation clarified

- Proof approval is an internal governed release decision, not evidence of external KDP acceptance.
- Digital and physical proofs share one authority and finding model, while physical approval additionally requires an inspected-artifact receipt and reviewer attestation.
- Checklist identity and version are part of the durable evidence boundary; changing a checklist changes the evidence digest.
- Corrections do not mutate frozen approved evidence. They require a governed superseding package/proof cycle.
- Exact replay and optimistic revision control are separate invariants: replay protects operation identity, while revision checks protect state transitions.

## Specification refinements retained

1. Revalidate exact VS-116 authority before evaluation, not only at initial submission.
2. Normalize and deterministically order findings before constructing output and evidence digests.
3. Bind physical receipt artifact identity to the approved package digest.
4. Persist checklist executions, findings, receipts, decisions, replay receipts, history and Outbox atomically.
5. Keep approval fail-closed for unresolved blocking findings and missing physical evidence.

## Follow-on contract

VS-118 may consume only an immutable approved VS-117 proof authority containing the exact proof identity, revision, package authority and evidence digest. Any drift or supersession must fail closed.

# VS-118 RetroSpec

## What the implementation clarified

1. A professional release is an internal immutable authority boundary, not evidence of external marketplace publication.
2. The approved VS-117 proof identity, revision, proof evidence digest, package identity and package digest must remain bound through submission and freeze.
3. Artifact metadata alone is insufficient; content digest and byte length must be verified before canonical assembly.
4. Deterministic ordering is part of the release contract because inventory, manifest and evidence identities depend on it.
5. Approval is a distinct transition after freeze and must match the exact frozen evidence digest.
6. Any later change must be represented as a governed superseding release rather than mutation of approved state.
7. Replay receipts, optimistic revision checks, append-only history and Outbox effects belong to one atomic durable transaction.

## Specification refinements retained

- Semantic version and channel are release authority inputs.
- Required and optional artifacts are explicit classifications.
- Freeze fails closed when required inventory or authority evidence is incomplete.
- Approved releases are immutable and supersession is explicit.
- Restart reconstruction and workspace isolation are normative behaviors.

## Deferred beyond VS-118

- External marketplace submission, status polling or publication confirmation.
- Distribution installation and operational rollout workflows unless assigned to a dependency-ready later slice.
- Cryptographic signing by an external trust service.

## Outcome

The delivered implementation remains aligned with the original VS-118 SDD while making the internal-versus-external publication boundary and immutable supersession semantics explicit.

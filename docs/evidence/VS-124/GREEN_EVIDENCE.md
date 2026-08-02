# VS-124 GREEN_EVIDENCE

Status: IMPLEMENTED — pending final same-head validation.

## GREEN-I
- Typed provider, quote, request, policy, output and evidence contracts.
- Atomic workspace-confined image and evidence persistence.
- Exact SHA-256, media type, byte size, provider/model/request and prompt lineage.
- Allowed-license, rights-holder, reference and territory enforcement.
- Alt-text evidence, cost ceiling and currency enforcement.
- Durable restart reuse only after exact digest verification.
- Bounded automatic repair.

## GREEN-E
`ImageProviderRightsPipelineSmoke` is wired into the integration executable and verifies a real SVG, exact evidence, restart reuse, invalid-rights rejection, excessive-cost rejection and repair-ceiling enforcement.

Final PASS requires Plan Integrity, Governance Gates and .NET CI green on one final SHA.
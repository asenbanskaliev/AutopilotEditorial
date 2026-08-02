# VS-125 GREEN evidence

Status: IMPLEMENTED — pending exact-head validation.

GREEN-I: provider-independent moderation and external rights-clearance contracts are integrated through `CommercialImageVerificationAuthority`, which decorates the existing image provider before `ImageProviderRightsPipeline` persists publication evidence.

GREEN-E: `CommercialImageVerificationAuthoritySmoke` is wired into the integration executable and proves approved verification, exact persisted evidence, restart reuse without external calls, unsafe rejection, uncleared-rights rejection and total-cost enforcement.

Final PASS requires Plan Integrity, Governance Gates and .NET CI green on one exact head SHA.
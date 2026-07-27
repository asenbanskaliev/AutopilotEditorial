# VS-034 — Model benchmarks and role constraints

## Status

`SPECIFICATION`

## Objective

Select an eligible model for an editorial role through versioned benchmark evidence, hard constraints, deterministic scoring and explicit fallbacks. The result must be provider-neutral, reproducible and auditable.

## Dependency

`VS-033 — Agent tool profiles` is merged and verified.

## Product boundary

VS-034 decides **which catalog model is eligible and preferred for a role**. It does not create sessions, send prompts, execute tools, compile context or benchmark live providers.

The decision flow is:

```text
role assignment request
→ benchmark catalog
→ freshness and evidence validation
→ hard eligibility constraints
→ deterministic weighted ranking
→ explicit fallback policy
→ ModelAssignmentDecision
→ provider mapping without capability expansion
```

## Application ownership

Application owns:

- provider-neutral model IDs and versions;
- benchmark dimensions and evidence metadata;
- role policies and hard constraints;
- weighted preferences;
- deterministic eligibility, ranking and tie-breaking;
- fallback chains;
- stable rejection/reason codes;
- assignment fingerprint and audit-safe explanation.

Application must not reference HTTP, provider SDK types, JSON DOM, API credentials, prompt contents or wall-clock APIs directly.

## Model benchmark catalog

Root:

```text
schemaVersion
catalogVersion
measuredAtEpochSeconds
models[]
rolePolicies[]
```

Each model record:

```text
modelId                   stable provider-neutral identifier
revision                  immutable positive integer
providerFamily            bounded provider family identifier
providerModelKey          bounded adapter lookup key
locality                   local | private_remote | public_remote
contextWindowTokens        positive integer
maximumOutputTokens        positive integer
inputCostMicrosPerMillion  non-negative integer
outputCostMicrosPerMillion non-negative integer
medianLatencyMs            positive integer
supportsStructuredOutput   boolean
supportsToolCalling        boolean
supportsVision             boolean
supportsReasoning          boolean
safetyTier                 1..5
benchmarkEvidence[]
```

Each benchmark evidence entry:

```text
dimension                  known benchmark dimension
scoreBasisPoints           0..10000
sampleCount                positive integer
confidenceBasisPoints      0..10000
measuredAtEpochSeconds     non-negative integer
sourceId                   bounded evidence source ID
sourceDigestSha256         lowercase SHA-256
```

Known first-version dimensions:

```text
long_form_coherence
instruction_following
structured_output
editing_accuracy
reasoning_quality
factuality
multilingual_quality
latency_efficiency
cost_efficiency
```

## Role policy

Each role policy:

```text
roleId
version
requiredDimensions[]
maximumEvidenceAgeSeconds
minimumConfidenceBasisPoints
minimumContextWindowTokens
minimumOutputTokens
maximumInputCostMicrosPerMillion
maximumOutputCostMicrosPerMillion
maximumMedianLatencyMs
minimumSafetyTier
allowedLocalities[]
requiresStructuredOutput
requiresToolCalling
requiresVision
requiresReasoning
weightsBasisPointsByDimension
fallbackModelIds[]
```

Rules:

- required dimensions must have evidence;
- mandatory evidence must be fresh relative to `evaluationEpochSeconds` supplied in the request;
- hard constraints always execute before weighted ranking;
- weights must sum exactly to 10000;
- dimensions with zero weight are omitted;
- a weighted dimension must be present and fresh for every ranked candidate;
- no provider fact may fill missing catalog evidence;
- fallback IDs are ordered and explicit;
- fallback never bypasses hard constraints;
- duplicate model, role, evidence dimension or fallback IDs are rejected.

## Assignment request

```text
roleId
rolePolicyVersion
evaluationEpochSeconds
requiredProfileFingerprint
availableProviderModels[]
optionalPreferredLocality
```

`availableProviderModels` contains only model IDs/revisions currently advertised by the provider compatibility layer. Availability may remove candidates but never add benchmark facts or relax constraints.

The `requiredProfileFingerprint` links the assignment to the already resolved VS-033 policy. It is copied into the assignment fingerprint but does not grant model capabilities.

## Eligibility

A candidate is eligible only when all are true:

- exact model ID/revision exists;
- provider currently advertises that exact model revision;
- all required evidence dimensions exist;
- each required/weighted evidence entry is fresh;
- confidence satisfies the role minimum;
- context/output limits satisfy minima;
- cost and latency satisfy maxima;
- safety tier satisfies minimum;
- locality is allowed;
- required structured output/tool calling/vision/reasoning flags are true.

Every rejection produces stable reason codes, not free-form provider data.

## Ranking

For each eligible candidate:

```text
weightedScore = Σ(scoreBasisPoints × weightBasisPoints) / 10000
```

Arithmetic uses checked integers and deterministic rounding toward zero.

Tie-breaking order:

1. higher weighted score;
2. preferred locality match when requested and allowed;
3. lower total configured input+output cost;
4. lower median latency;
5. lexicographically lower `modelId` using ordinal comparison;
6. higher revision only when model IDs are equal.

The same catalog, request and provider availability must produce the same decision and fingerprint.

## Fallback behavior

- Normal ranking chooses the best eligible candidate.
- If no ranked candidate exists, iterate `fallbackModelIds` in policy order.
- A fallback is selected only if it remains fully eligible.
- A rejected fallback records its reason codes and evaluation continues.
- If no fallback is eligible, fail closed.
- There is no implicit “first available”, cheapest-model or provider-default fallback.

## Assignment result

```text
catalogVersion
roleId
rolePolicyVersion
selectedModelId
selectedRevision
providerFamily
providerModelKey
selectionMode              ranked | fallback
weightedScoreBasisPoints
profileFingerprint
assignmentFingerprint
reasonCodes[]
eligibleCandidateCount
```

The constructor is internal to Application. The assignment originates only from the selector.

## OpenCode mapping

The OpenCode adapter maps an assignment to a provider model reference only when:

- assignment fingerprint verifies;
- exact model ID/revision is still advertised;
- provider family and model key exactly match catalog facts;
- provider does not claim missing capabilities on behalf of the model;
- mapping does not change role, policy, selected model or selection mode.

Mapping is pure and performs no network call or session mutation.

## Stable errors

```text
model_benchmark_invalid
model_benchmark_catalog_not_found
model_role_policy_not_found
model_role_policy_version_not_found
model_benchmark_missing_evidence
model_benchmark_stale_evidence
model_benchmark_low_confidence
model_assignment_no_eligible_model
model_assignment_provider_unavailable
model_assignment_profile_fingerprint_invalid
model_assignment_provider_unsupported
model_assignment_limits_invalid
```

## Repository catalog

The first repository catalog must include enough models and roles to prove:

- long-form authoring;
- structural/editorial review;
- quality auditing;
- release/structured-output preparation;
- local-only fallback behavior.

Values are contractual test fixtures, not public claims about current commercial model performance.

## Security invariants

- no eligibility without explicit evidence;
- stale mandatory evidence fails closed;
- hard constraints override score and fallback;
- provider availability can only narrow;
- assignment constructors and fingerprint computation are not public trust bypasses;
- no prompts, benchmark samples, API keys or raw provider bodies in evidence;
- selection performs no remote mutation;
- all lists, scores, costs, times and payloads are bounded.

## TDD Dual

### RED-I

Governance must fail until contracts, catalog/schema, selector, fingerprint, provider mapper, architecture and CI exist.

### RED-E

The real journey must fail until it proves:

1. strict repository catalog load;
2. role policy lookup/versioning;
3. hard constraint filtering;
4. missing evidence rejection;
5. stale evidence rejection;
6. low-confidence rejection;
7. deterministic weighted ranking;
8. deterministic tie-breaking;
9. explicit fallback order;
10. fallback cannot bypass hard constraints;
11. provider availability only narrows;
12. assignment/profile fingerprint validation;
13. provider mapping cannot broaden facts;
14. concurrency, cancellation and no remote mutation.

## Acceptance gates

```text
MODEL_CATALOG_SCHEMA_PASS
BENCHMARK_EVIDENCE_PASS
HARD_CONSTRAINTS_PASS
DETERMINISTIC_RANKING_PASS
STALE_EVIDENCE_REJECTED_PASS
FALLBACK_POLICY_PASS
PROVIDER_NEUTRAL_PASS
NO_REMOTE_MUTATION_PASS
DUAL_GREEN
M_AUDIT_PASS
META_AUDIT_PASS
RETROSPEC_PASS
```

## Out of scope

- live benchmark execution;
- automatic downloading of model cards;
- current market price discovery;
- API key management;
- session/prompt execution;
- context compilation;
- adaptive online learning;
- administrative UI.

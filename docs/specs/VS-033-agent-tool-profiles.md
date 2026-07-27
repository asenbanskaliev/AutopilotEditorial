# VS-033 — Agent tool profiles

## Status

`SPECIFICATION`

## Objective

Provide provider-neutral, versioned and auditable tool profiles per editorial workflow and agent role, with deterministic resolution, deny-by-default enforcement and no privilege escalation across OpenCode sessions.

## Dependency

`VS-032 — SSE reconciliation` is `VERIFIED`, including audit remediation #48/#49.

## Product boundary

A workflow requests a named profile. Application resolves one immutable effective profile before any prompt/session execution. OpenCode transport receives only the resolved provider-neutral permissions; workflow code must not construct provider-specific tool payloads.

## Application ownership

Application owns:

- profile identifiers and versions;
- workflow and role selectors;
- capability/tool policy contracts;
- effective profile resolution;
- validation and stable rejection codes;
- audit-safe resolution result.

Application must not reference HTTP, JSON DOM, provider SDK unions, filesystem enumeration, process execution or credentials.

## Profile contract

```text
profileId                 bounded stable identifier
version                   positive integer
workflow                  bounded workflow identifier
role                      bounded role identifier
allowedCapabilities[]     explicit allowlist
allowedTools[]            explicit allowlist
forbiddenCapabilities[]   explicit denylist
forbiddenTools[]          explicit denylist
requiresHumanApproval     boolean
maximumToolCalls          bounded positive integer
maximumParallelTools      bounded positive integer
```

Rules:

- absence from the allowlist means denied;
- deny always overrides allow;
- duplicate entries are rejected after canonical comparison;
- unknown capabilities/tools are rejected;
- profile IDs and versions are immutable once published;
- no wildcard, prefix or regex grants are allowed in the first implementation;
- limits cannot exceed centrally configured product ceilings.

## Resolution

```text
Resolve(profileId, workflow, role, requestedCapabilities, requestedTools)
→ EffectiveAgentToolProfile
```

Resolution order:

1. validate request bounds;
2. load exact profile ID/version;
3. require workflow match;
4. require role match;
5. canonicalize requested capabilities/tools;
6. reject unknown values;
7. apply deny lists;
8. require every remaining requested value to be explicitly allowed;
9. clamp operational limits to product ceilings;
10. emit immutable effective profile plus audit fingerprint.

Resolution is deterministic: the same catalog version and request produce the same effective profile and fingerprint.

## Privilege rules

- a child workflow may only narrow its parent effective profile;
- role changes never inherit permissions implicitly;
- human approval cannot be disabled by a child profile;
- tool-call and parallelism limits may only decrease downstream;
- provider compatibility cannot expand a profile;
- missing provider support fails closed.

## OpenCode integration

The OpenCode adapter maps the effective provider-neutral profile to the supported request shape detected by VS-030. Mapping is one-way and must not mutate the effective profile.

The adapter must reject execution when:

- the provider cannot represent an enforced deny;
- a requested tool is unsupported;
- the resolved profile fingerprint is absent or invalid;
- provider mapping would broaden permissions.

## Stable rejection codes

```text
agent_profile_invalid
agent_profile_not_found
agent_profile_version_not_found
agent_profile_workflow_mismatch
agent_profile_role_mismatch
agent_profile_unknown_capability
agent_profile_unknown_tool
agent_profile_permission_denied
agent_profile_privilege_escalation
agent_profile_provider_unsupported
agent_profile_limits_invalid
```

Messages must not expose provider payloads, credentials, prompts or internal catalog paths.

## Persistence and versioning

- catalog input is repository-controlled and schema-validated;
- published versions are append-only;
- effective profiles contain catalog version and SHA-256 audit fingerprint;
- runtime caches are bounded and invalidated by catalog version;
- no database is introduced in this slice.

## Security invariants

- deny by default;
- deny overrides allow;
- exact matching only;
- no provider capability may broaden policy;
- no execution before successful resolution;
- no raw prompt, credential or provider body in evidence/logs;
- profile resolution performs no remote mutation.

## TDD Dual

### RED-I

Governance must fail until contracts, schema, resolver, provider mapping boundary, architecture registration and CI evidence exist.

### RED-E

The contractual journey must fail until it proves:

- valid workflow/role resolution;
- deny-by-default;
- deny-overrides-allow;
- unknown tool/capability rejection;
- child-profile narrowing;
- privilege-escalation blocking;
- limit clamping;
- deterministic fingerprinting;
- provider mapping cannot broaden permissions;
- cancellation and concurrency safety;
- no mutation before resolution.

## Acceptance gates

```text
PROFILE_SCHEMA_PASS
DENY_BY_DEFAULT_PASS
WORKFLOW_RESOLUTION_PASS
UNKNOWN_TOOL_REJECTED_PASS
PRIVILEGE_ESCALATION_BLOCKED_PASS
PROVIDER_NEUTRAL_PASS
NO_MUTATION_OUTSIDE_PROFILE_PASS
DUAL_GREEN
M_AUDIT_PASS
META_AUDIT_PASS
RETROSPEC_PASS
```

## Out of scope

- model benchmarking and role/model assignment (VS-034);
- context compilation (VS-035);
- dynamic marketplace installation;
- arbitrary user-authored scripts;
- durable policy administration UI;
- cross-tenant profile sharing.

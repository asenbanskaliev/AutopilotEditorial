# VS-033 — RetroSpec

## Implemented boundary

VS-033 introduces deterministic, repository-controlled tool profiles between editorial workflow intent and OpenCode provider mapping.

```text
workflow + role + requested policy
→ Application resolver
→ EffectiveAgentToolProfile
→ OpenCode mapper
→ exact provider allow/deny policy
```

No execution, session mutation or prompt submission is part of this slice.

## Catalog format

The repository catalog is `config/opencode/agent-tool-profiles.json`, governed by `agent-tool-profiles.schema.json`.

Root:

```text
schemaVersion
catalogVersion
profiles[]
```

Each profile contains:

```text
profileId
version
workflow
role
allowedCapabilities[]
allowedTools[]
forbiddenCapabilities[]
forbiddenTools[]
requiresHumanApproval
maximumToolCalls
maximumParallelTools
```

Constraints:

- exact bounded identifiers;
- known capabilities/tools only;
- unique entries;
- unique profile ID/version pairs;
- positive bounded limits;
- append-only publication is the operational policy.

## Resolution contract

```text
Resolve(request, cancellationToken)
```

Order:

1. validate identifiers and version;
2. canonicalize requested capabilities/tools;
3. reject unknown values;
4. load exact profile and version;
5. match workflow and role exactly;
6. reject any explicitly denied value;
7. reject any value not explicitly allowed;
8. verify optional parent fingerprint and catalog version;
9. require requested values to be subsets of the parent;
10. preserve/increase human approval requirement;
11. clamp limits against product and parent limits;
12. generate an immutable effective profile and SHA-256 fingerprint.

## Effective profile

The effective result contains:

```text
catalogVersion
profileId
profileVersion
workflow
role
allowedCapabilities[]
allowedTools[]
requiresHumanApproval
maximumToolCalls
maximumParallelTools
parentFingerprint?
fingerprint
```

The constructor is internal. Effective profiles originate from the resolver, not arbitrary callers.

Equality is structural and consistent with `==`, `!=`, `Equals` and fingerprint determinism.

## Parent narrowing

A child may change workflow and role only by selecting another explicit catalog profile. It may not gain any capability/tool absent from the parent effective allowlists.

```text
childCapabilities ⊆ parentCapabilities
childTools        ⊆ parentTools
childApproval     = childApproval OR parentApproval
childToolCalls    = min(profile, product, parent)
childParallel     = min(profile, product, parent)
```

An invalid parent fingerprint or different catalog version fails with `agent_profile_privilege_escalation`.

## Provider mapping

The mapper receives explicit provider support:

```text
SupportedTools
SupportsDenyByDefault
SupportsExplicitDeny
MaximumToolEntries
```

It succeeds only when:

- the effective fingerprint verifies;
- deny-by-default is representable;
- explicit deny is representable;
- every allowed tool is supported;
- the full supported inventory fits the provider bound.

Output:

```text
AllowedTools = effective allowed tools
DeniedTools  = supported tools - allowed tools
DenyByDefault = true
```

The mapped constructor is internal and the mapper performs no transport operation.

## Stable errors

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

Messages equal the stable code and do not echo rejected values.

## Repository profiles

The first catalog version contains:

- `artifact-reader`;
- `draft-author`;
- `quality-auditor`;
- `release-producer`;
- `operations-observer`.

## Executable proof

```text
OPENCODE_AGENT_TOOL_PROFILES_PASS scenarios=12 profiles=5 fingerprints=6 gate=NO_PRIVILEGE_ESCALATION mutation=NONE
```

The journey proves repository loading, selectors, deny semantics, unknown rejection, deterministic equality/fingerprints, child narrowing, monotonic limits/approval, fail-closed provider mapping, concurrency, cancellation and safe evidence.

## Deliberate omissions

- model/role assignment belongs to VS-034;
- context compilation belongs to VS-035;
- actual OpenCode session/prompt execution belongs to later integration slices;
- hot reload and policy administration UI are not implemented;
- the fingerprint is not a cross-process signature;
- operating-system sandboxing is not provided by this policy object.

## Phase result

VS-033 satisfies its implemented contract after final audited-head validation. The full program remains `NOT_READY`.

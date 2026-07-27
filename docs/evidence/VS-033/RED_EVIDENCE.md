# VS-033 — RED evidence

## Result

`EXPECTED_RED`

## RED-I — Governance / integration

The slice specification now requires the following artifacts, which do not yet exist:

- provider-neutral Application contracts for agent tool profiles;
- immutable catalog/profile schema;
- deterministic resolver and audit fingerprint;
- child-profile narrowing and privilege-escalation guard;
- OpenCode mapping boundary that cannot broaden permissions;
- architecture registration and CI contract;
- governance tests for all acceptance gates.

Expected failures:

```text
PROFILE_SCHEMA_MISSING
PROFILE_RESOLVER_MISSING
PRIVILEGE_GUARD_MISSING
OPENCODE_PROFILE_MAPPING_MISSING
ARCHITECTURE_REGISTRATION_MISSING
CI_CONTRACT_MISSING
```

## RED-E — Contractual journey

No executable journey currently proves:

1. exact profile/workflow/role resolution;
2. deny-by-default behavior;
3. deny-overrides-allow behavior;
4. unknown tool/capability rejection;
5. deterministic canonicalization and fingerprinting;
6. child profile can only narrow permissions;
7. human approval and operational limits cannot be weakened;
8. provider feature detection cannot expand permissions;
9. unsupported provider mappings fail closed;
10. no OpenCode mutation occurs before successful resolution;
11. cancellation and concurrent resolution do not leak work;
12. logs/evidence exclude prompts, credentials and provider payloads.

Expected journey marker is absent:

```text
OPENCODE_AGENT_TOOL_PROFILES_PASS scenarios=12 gate=NO_PRIVILEGE_ESCALATION mutation=NONE
```

## Dependency baseline

The branch is based on `main` commit `93cc967730aff406419ef76fe63fe7396a5872c9`, which includes the bounded VS-032 status-history remediation.

## Transition to GREEN

The slice may leave RED only after both governance and the real contractual journey pass, followed by Auditoría M, Meta-Audit and RetroSpec. A mocked-only resolver is insufficient.

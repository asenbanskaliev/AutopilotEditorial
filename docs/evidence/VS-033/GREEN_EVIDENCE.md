# VS-033 — GREEN Evidence

## Result

```text
OPENCODE_AGENT_TOOL_PROFILES_PASS scenarios=12 profiles=5 fingerprints=6 gate=NO_PRIVILEGE_ESCALATION mutation=NONE
```

## Functional head before final audit hardening

```text
aa48d3dd3f2762a01c8efc8cdbc55cec8885742f
```

## Gates

- Plan Integrity: run `30306587107` — PASS.
- Governance Gates: run `30306587103` — PASS.
- Governance artifact: `8668657340`.
- Governance digest: `sha256:274bd6f68da57ffa5b9e1c954820584f48e459a72f1fb7191d912273876d230f`.
- .NET CI: run `30306587101` — PASS.
- .NET job: `90112209197`.
- .NET artifact: `8668701240`.
- .NET digest: `sha256:60bfe79d726b89f4b561975d847341d084bd05d1af06e3230429cf0b936ba475`.

## Normalized contract

- contract: `dotnet.agent-tool-profiles-integration`;
- result: `PASS`;
- exit code: `0`;
- duration: `776 ms`;
- stdout SHA-256: `28c2f7fef495a4581242f9ea513d53a4ddf03ec19a041fb942324ddf6a183659`;
- stderr SHA-256: `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`;
- stderr: empty;
- retry chain: empty.

## Verified capabilities

- strict repository catalog load;
- immutable bounded catalog;
- exact profile/version/workflow/role resolution;
- deny-by-default;
- deny-overrides-allow;
- stable unknown-value rejection;
- canonical order and deterministic SHA-256;
- structural equality of equivalent effective profiles;
- child subset enforcement;
- monotonic human approval;
- product and parent limit clamping;
- fail-closed provider support mapping;
- bounded concurrency and cancellation;
- no network/provider mutation;
- safe rejection evidence;
- architecture and CI registration;
- all accumulated journeys remain green.

## Audit hardening

After this GREEN run, Auditoría M restricted effective/mapped constructors and fingerprint computation to their owning assemblies so the audit hash cannot be mistaken for an external authorization token. The final audited head must re-run the same three gates before merge.

## Program state

- VS-033: `VERIFIED_PENDING_FINAL_HEAD`.
- Full program: `NOT_READY`.

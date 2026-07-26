# VS-011 — Dual Red Evidence

## RED-I

GitHub Actions `Governance Gates` run `30210414802`, job `89815572682`, failed in the governance test step after plan integrity, completion policy and CI-provider validation passed.

Expected missing behavior:

- no canonical `architecture-policy.json`;
- no ADR-001;
- no scoped AGENTS files;
- no policy-driven static validation.

## RED-E

The current architecture executable still embeds the reference graph in C# and only checks project XML. It does not load a versioned policy or inspect compiled PE assembly references.

## Confirmation

- governance evidence was still generated and uploaded despite the expected RED;
- the failure is isolated to the missing architecture-boundary behavior;
- the existing .NET solution remains buildable from VS-010.

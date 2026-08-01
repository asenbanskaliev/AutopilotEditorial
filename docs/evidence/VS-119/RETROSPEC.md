# VS-119 — RetroSpec

Status: PASS pending same-head CI confirmation.

## What changed from the initial assumption

The project already persisted a `LanguageTag`, but generation boundaries did not carry an immutable language authority or enforce post-generation conformance. VS-119 therefore treats language as governed domain evidence, not merely a prompt hint.

## Decisions retained

- `BookLanguageTag` is authoritative for generated book content.
- UI language is independent and may differ safely.
- Internal prompts may use any language, while output language is contractually fixed.
- Locale variants are explicit profiles rather than aliases.
- Intentional multilingual content requires bounded approved scopes.
- Acceptance uses both compiled instructions and post-generation validation.

## Operational learning

Prompt-only control is insufficient. Deterministic policy digests, detector evidence, fail-closed decisions and durable replay are required to prevent silent language drift across planning, drafting, rewriting, editing, metadata and production.

## Follow-up boundary

Future locale expansion should add profile data and tests without changing the existing authority, replay or approval invariants.

Conclusion: PASS, subject to all three required workflows succeeding on the exact final SHA.

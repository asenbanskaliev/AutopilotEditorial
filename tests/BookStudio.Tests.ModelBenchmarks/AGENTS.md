# BookStudio.Tests.ModelBenchmarks Agent Rules

## Allowed

- Use the real Application benchmark catalog/selector and real OpenCode loader/mapper.
- Use only deterministic fixture timestamps supplied by the scenarios.
- Assert exact stable error/reason codes and fingerprints.
- Exercise repository catalog parsing, hard constraints, ranking, fallback and provider mapping.
- Run bounded concurrent selection and await every task.
- Record only aggregate counts and sanitized gates.

## Forbidden

- Do not query live model APIs, prices, benchmark sites or provider metadata.
- Do not mock the selector or provider mapper.
- Do not use wall-clock time, randomness or provider defaults.
- Do not print prompts, credentials, benchmark samples or provider payloads.
- Do not remove or weaken failed scenarios without a TestChangeRequest.
- Do not perform session, prompt or remote mutations.

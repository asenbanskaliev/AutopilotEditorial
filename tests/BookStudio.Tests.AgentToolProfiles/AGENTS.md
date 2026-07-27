# BookStudio.Tests.AgentToolProfiles Agent Rules

## Allowed

- Use the real Application catalog/resolver and the real OpenCode loader/mapper.
- Load only the explicit repository-controlled profile JSON used by the scenario.
- Assert stable rejection codes, deterministic fingerprints, limit monotonicity and exact policy mapping.
- Run bounded concurrent resolution and await every task.
- Record only sanitized gate markers and aggregate scenario counts.

## Forbidden

- Do not replace policy resolution or provider mapping with mocks.
- Do not contact networks, create processes, enumerate directories or mutate provider/session state.
- Do not print prompts, credentials, provider payloads, internal catalog paths or rejected sensitive values.
- Do not remove or weaken a failed scenario without a TestChangeRequest.
- Do not leave background work running after a scenario.

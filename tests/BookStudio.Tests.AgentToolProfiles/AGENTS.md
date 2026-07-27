# Agent instructions — Agent tool profiles journey

## Ownership

This project owns the executable dual-TDD journey for VS-033.

## Invariants

- Use the real Application catalog/resolver and the real OpenCode loader/mapper.
- Do not replace policy resolution with mocks.
- No network, process, filesystem enumeration or provider mutation is permitted.
- Catalog loading may read only the explicit repository-controlled JSON file used by the scenario.
- Every rejection must be asserted by stable code, never by sensitive message content.
- Concurrency scenarios must await every task.
- A failed scenario may not be deleted or weakened without a TestChangeRequest.

# Control Center Instructions

## Allowed

- UI, HTTP endpoints, composition and presentation models.
- Invoke Application use cases.
- Display workflow state, evidence, decisions and errors.
- References to Application and Infrastructure for host composition.

## Forbidden

- Direct persistence queries or writes from presentation code.
- Domain decisions in controllers or views.
- Hidden technical commands required for normal journeys.
- Secrets in browser payloads or logs.
- Treating UI state as canonical workflow state.

Every user action must map to an explicit use case and actionable error contract.

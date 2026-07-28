# VS-050 RED Evidence

Before this slice the repository exposed editorial tools and Autopilot infrastructure, but it did not provide one durable project-creation journey that could anchor all later authoring phases.

RED scenarios:

- A UI/application request could not create and retrieve a project as one coherent vertical flow.
- Identical create replay had no defined idempotency contract.
- Reused request/project identities could not fail closed on changed content.
- Workspace isolation was unproven.
- Project persistence and `editorial.project.created` delivery intent were not atomic.
- Restart durability was not covered by an end-to-end journey.

These scenarios define the failing baseline for VS-050.

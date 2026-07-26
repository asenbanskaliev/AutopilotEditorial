# Integration Test Instructions

## Allowed

- Exercise real Infrastructure adapters through temporary workspaces.
- Create temporary databases, files and process-local resources.
- Validate Application contracts and operational recovery.
- Fail with precise evidence and return a non-zero exit code.
- Reference Application and Infrastructure.

## Forbidden

- Network or external-provider dependencies unless the slice explicitly requires them.
- Persistent test data outside a temporary workspace.
- Mocks that replace the adapter under test.
- Product behavior helpers or production migrations created only for tests.
- Treating cleanup failures as product failures after all handles are released.

Every integration journey must be deterministic and runnable from a clean checkout.

# Transactional Outbox — Preimplementation Evidence

This evidence originated in issue #14 and PR #15, which were incorrectly labelled as canonical `VS-014`. The immutable backlog defines that slice as `API and health`; therefore this evidence is retained only as regression history for future `VS-040 — Transactional Outbox` certification.

## Original RED

- Governance run `30212896805`, job `89822025244`.
- Missing Outbox contracts, migration, store and lease/retry journey.

## Original GREEN

- .NET run `30213286624`, job `89823023585`.
- Cross-store hardened run `30213360913`, job `89823219952`.
- Final run `30213469453`, job `89823506871`.
- Evidence artifact `8635099953`.
- Digest `sha256:2852a8bb791fc703b8e66292ad3a1a560714039c4d72ce6ef0c9968f9719b807`.

## Certification restriction

These checks prove the current implementation behavior but do not satisfy the canonical dependency, integration and audit contract of VS-040. They remain active regression coverage.

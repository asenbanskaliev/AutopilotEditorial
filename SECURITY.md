# Security Policy

## Supported release

Security fixes apply to the latest release on `main`. Older snapshots are not maintained unless explicitly stated in release notes.

## Reporting a vulnerability

Do not open a public issue containing credentials, personal data, exploit details or unpublished manuscripts. Report the minimum reproducible information privately to the repository owner and include affected version, impact, reproduction steps and suggested mitigation when known.

## Credential handling

BookStudio credentials must be provided through ephemeral process environment or an approved secret store. Credentials must never be committed, written to evidence, included in generated books, embedded in publication packages or persisted in OpenCode runtime directories. CI masks live credentials and verifies that evidence does not contain them.

## Manuscript and privacy handling

Manuscripts, prompts and editorial evidence may contain confidential or personal information. Operators must apply least privilege, restrict retention, remove unnecessary personal data and avoid sending material to providers without authorization. Advertisers do not receive repository evidence or manuscripts through this product.

## Supply chain

Runtime and CI dependencies must be pinned or centrally versioned. Dependency upgrades require tests, provenance review and a security assessment appropriate to the change.

## Response expectations

A valid report should receive acknowledgement within seven calendar days. Critical reports should be triaged before normal feature work. Public disclosure must wait until a mitigation is available or a coordinated disclosure date has been agreed.

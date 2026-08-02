# M Audit — VS-126

## Scope
Windows installation and first-run authority only.

## Findings
- Security: fail-closed digest and signature validation; DPAPI current-user credential protection; path confinement.
- Durability: atomic JSON checkpoints after each phase; restart resumes from the last completed phase.
- Cost: mandatory non-negative monthly EUR ceiling persisted in state and evidence.
- Repair: configurable finite ceiling, then manual-review failure.
- UX: guided prompts or deployment environment inputs; successful setup launches the application automatically.
- Evidence: package digest, signer, signature status, root, provider, budget, credential mechanism and repair count.

## Residual risks
- Signing and packaging workflow must supply an Authenticode-signed archive.
- Windows end-to-end execution requires validation on a Windows runner.
- DPAPI binds secrets to the installing user; service-account deployment needs an explicit account strategy.

Decision: ready for validation, not ready for merge until all gates pass on one final SHA.

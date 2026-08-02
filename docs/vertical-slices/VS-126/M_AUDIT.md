# M Audit — VS-126

## Scope
Windows installation and first-run authority only.

## Findings
- Security: fail-closed digest and Authenticode validation; DPAPI current-user credential protection; path confinement; no Root-store mutation.
- Durability: atomic JSON checkpoints after each phase; restart resumes from the last completed phase and completed setup is not repeated.
- Cost: mandatory non-negative monthly EUR ceiling persisted in state and evidence.
- Repair: configurable finite ceiling, bounded process execution and manual-review failure after exhaustion.
- UX: guided prompts or deployment environment inputs; successful setup launches the real installed application without technical commands.
- Runtime proof: the installed `BookStudio.ControlCenter` answered `/health/live` on the exact configured loopback URL.
- Evidence: package digest, signer, signature status, root, provider, budget, credential mechanism, repair count, PID, command, effective URL, exit code, stdout and stderr.

## Exact validation
Implementation head `d4b2e168040972b209b722444803a99f48e14e58`:
- Plan Integrity run `30763347809`: PASS.
- Governance Gates run `30763347810`: PASS.
- .NET CI Windows installer E2E run `30763347829`: PASS.

## Residual risks
- Production release packaging still requires an organizational Authenticode certificate and protected signing workflow.
- DPAPI binds secrets to the installing user; service-account deployment needs an explicit account strategy.
- The supported-Windows-version execution matrix and independent penetration testing remain release-hardening work.

Decision: VS-126 behavior is proven on the exact implementation head and is ready for final PR validation. Merge remains prohibited by the active workflow policy.

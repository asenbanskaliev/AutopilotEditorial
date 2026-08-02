# GREEN evidence — VS-126

Implemented and executable authority:
- exact SHA-256 package verification;
- mandatory Authenticode signature validation;
- workspace confinement checks;
- atomic phase checkpoint and restart resume;
- guided provider and monthly-cost setup;
- DPAPI-protected credential persistence;
- idempotent completed-installation behavior;
- bounded repair attempts with fail-closed manual-review transition;
- exact installation evidence and automatic application launch;
- real installed `BookStudio.ControlCenter` startup and `/health/live` proof;
- exact failure diagnostics containing PID, command, effective URL, exit code, stdout and stderr.

Executable governance coverage is provided by `tests/governance/test_signed_installer_first_run_contract.py` and the Windows installer E2E in `tests/installer/Invoke-InstallerE2E.ps1`.

Exact validated implementation head: `d4b2e168040972b209b722444803a99f48e14e58`.

Validation evidence on that exact head:
- Plan Integrity: PASS — workflow run `30763347809`;
- Governance Gates: PASS — workflow run `30763347810`;
- .NET CI Windows + installer E2E: PASS — workflow run `30763347829`.

Observed E2E result: the real Control Center was published, digest-bound into the package, installed, launched on the configured loopback URL, answered `/health/live`, and preserved restart idempotency without repeating completed setup.

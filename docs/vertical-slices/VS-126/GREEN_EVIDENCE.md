# GREEN evidence — VS-126

Implemented authority:
- exact SHA-256 package verification;
- mandatory valid Authenticode signature;
- workspace confinement checks;
- atomic phase checkpoint and restart resume;
- guided provider and monthly-cost setup;
- DPAPI-protected credential persistence;
- idempotent completed-installation behavior;
- bounded repair attempts with fail-closed manual-review transition;
- exact installation evidence and automatic application launch.

Executable governance coverage is provided by `tests/governance/test_signed_installer_first_run_contract.py`.

Validation status on this checkpoint: pending Plan Integrity, Governance Gates and .NET CI on the final SHA.

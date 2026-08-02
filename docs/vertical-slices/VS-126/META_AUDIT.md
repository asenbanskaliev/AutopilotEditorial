# Meta-Audit — VS-126

VS-126 closes the recorded installation gap through the executable installer authority rather than through an isolated contract. The Windows E2E publishes the real `BookStudio.ControlCenter`, packages it, verifies digest and Authenticode evidence, installs it into a confined root, protects provider credentials with DPAPI, enforces a monthly cost ceiling, launches the installed executable, probes `/health/live`, and repeats installation to prove restart idempotency.

Claims are bounded to evidence observed on exact implementation head `d4b2e168040972b209b722444803a99f48e14e58`:
- Plan Integrity run `30763347809`: PASS;
- Governance Gates run `30763347810`: PASS;
- .NET CI Windows installer E2E run `30763347829`: PASS.

The previous false-negative health failure was traced to a mismatch between the URL configured by `ControlCenter:Url` and the URL queried by the test. The repair injects `ControlCenter__Url` and `ControlCenter__WorkspaceRoot` into the installed process and captures exact runtime diagnostics on failure.

No second VS, branch or PR was created. No merge was performed. Production signing infrastructure, supported-Windows breadth and independent penetration testing remain explicitly outside the proven claim.

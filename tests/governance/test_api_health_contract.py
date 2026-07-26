from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

REQUIRED = [
    ROOT / "src/BookStudio.Application/Diagnostics/IReadinessProbe.cs",
    ROOT / "src/BookStudio.Infrastructure/Diagnostics/WorkspaceDatabaseReadinessProbe.cs",
    ROOT / "src/BookStudio.ControlCenter/ControlCenterHostOptions.cs",
    ROOT / "src/BookStudio.ControlCenter/ControlCenterApplication.cs",
    ROOT / "src/BookStudio.ControlCenter/WorkspaceDatabaseInitializationService.cs",
    ROOT / "tests/BookStudio.Tests.Api/BookStudio.Tests.Api.csproj",
    ROOT / "tests/BookStudio.Tests.Api/Program.cs",
    ROOT / "tests/BookStudio.Tests.Api/AGENTS.md",
]


class ApiHealthContractTests(unittest.TestCase):
    def test_required_api_health_files_exist(self) -> None:
        for path in REQUIRED:
            self.assertTrue(path.exists(), f"Missing API/health contract: {path}")

    def test_control_center_declares_required_routes_and_safe_binding(self) -> None:
        content = (ROOT / "src/BookStudio.ControlCenter/ControlCenterApplication.cs").read_text(
            encoding="utf-8"
        )
        for route in ("/health/live", "/health/ready", "/api/v1/diagnostics", "/health"):
            self.assertIn(route, content)
        options = (ROOT / "src/BookStudio.ControlCenter/ControlCenterHostOptions.cs").read_text(
            encoding="utf-8"
        )
        self.assertIn("127.0.0.1", options)
        self.assertIn("AllowRemoteBinding", options)

    def test_ci_catalog_contains_api_health_integration_contract(self) -> None:
        data = json.loads((ROOT / "config/ci/providers.json").read_text(encoding="utf-8"))
        contracts = {item["id"]: item for item in data["contracts"]}
        self.assertIn("dotnet.api-health-integration", contracts)
        self.assertEqual("integration", contracts["dotnet.api-health-integration"]["capability"])

    def test_early_outbox_registry_preserves_canonical_slice_identity(self) -> None:
        registry = (ROOT / "docs/execution/EARLY_CAPABILITIES.md").read_text(encoding="utf-8")
        self.assertIn("VS-040", registry)
        self.assertIn("PREIMPLEMENTED_NOT_CERTIFIED", registry)
        self.assertIn("does not mark `VS-014`", registry)


if __name__ == "__main__":
    unittest.main()

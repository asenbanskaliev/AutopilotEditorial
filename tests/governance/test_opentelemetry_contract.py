from __future__ import annotations

import json
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PACKAGES = ROOT / "Directory.Packages.props"
CONTROL_CENTER = ROOT / "src/BookStudio.ControlCenter/BookStudio.ControlCenter.csproj"

REQUIRED = [
    ROOT / "src/BookStudio.Application/Observability/BookStudioTelemetry.cs",
    ROOT / "src/BookStudio.Application/Observability/IObservabilitySnapshotReader.cs",
    ROOT / "src/BookStudio.Application/Observability/ObservabilitySnapshot.cs",
    ROOT / "src/BookStudio.Infrastructure/Observability/TelemetrySnapshotStore.cs",
    ROOT / "src/BookStudio.Infrastructure/Observability/SnapshotActivityExporter.cs",
    ROOT / "src/BookStudio.Infrastructure/Observability/SnapshotMetricExporter.cs",
    ROOT / "src/BookStudio.Infrastructure/Observability/SnapshotLogExporter.cs",
    ROOT / "src/BookStudio.ControlCenter/ObservabilityOptions.cs",
    ROOT / "src/BookStudio.ControlCenter/OpenTelemetryConfiguration.cs",
]

OTEL_PACKAGES = {
    "OpenTelemetry",
    "OpenTelemetry.Extensions.Hosting",
    "OpenTelemetry.Exporter.OpenTelemetryProtocol",
    "OpenTelemetry.Instrumentation.AspNetCore",
    "OpenTelemetry.Instrumentation.Runtime",
}


class OpenTelemetryContractTests(unittest.TestCase):
    def test_required_observability_files_exist(self) -> None:
        for path in REQUIRED:
            self.assertTrue(path.exists(), f"Missing observability contract: {path}")

    def test_opentelemetry_packages_are_centrally_pinned_to_1_17_0(self) -> None:
        root = ET.fromstring(PACKAGES.read_text(encoding="utf-8"))
        versions = {
            item.attrib["Include"]: item.attrib["Version"]
            for item in root.findall(".//PackageVersion")
        }
        for package in OTEL_PACKAGES:
            self.assertEqual("1.17.0", versions[package], package)

    def test_control_center_references_otel_packages_without_inline_versions(self) -> None:
        root = ET.fromstring(CONTROL_CENTER.read_text(encoding="utf-8"))
        references = {
            item.attrib["Include"]: item.attrib
            for item in root.findall(".//PackageReference")
        }
        for package in OTEL_PACKAGES - {"OpenTelemetry"}:
            self.assertIn(package, references)
            self.assertNotIn("Version", references[package])

    def test_snapshot_redaction_policy_contains_required_sensitive_terms(self) -> None:
        content = (ROOT / "src/BookStudio.Infrastructure/Observability/TelemetrySnapshotStore.cs").read_text(
            encoding="utf-8"
        ).lower()
        for term in (
            "password",
            "secret",
            "token",
            "authorization",
            "cookie",
            "path",
            "prompt",
            "content",
            "connection",
        ):
            self.assertIn(term, content)

    def test_control_center_declares_observability_endpoint(self) -> None:
        content = (ROOT / "src/BookStudio.ControlCenter/ControlCenterApplication.cs").read_text(
            encoding="utf-8"
        )
        self.assertIn("/api/v1/observability", content)

    def test_ci_catalog_contains_observability_integration_contract(self) -> None:
        data = json.loads((ROOT / "config/ci/providers.json").read_text(encoding="utf-8"))
        contracts = {item["id"]: item for item in data["contracts"]}
        self.assertIn("dotnet.opentelemetry-integration", contracts)
        self.assertEqual("integration", contracts["dotnet.opentelemetry-integration"]["capability"])


if __name__ == "__main__":
    unittest.main()

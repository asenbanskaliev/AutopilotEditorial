from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "tests/BookStudio.Tests.McpConformance"
REQUIRED = [
    PROJECT / "AGENTS.md",
    PROJECT / "BookStudio.Tests.McpConformance.csproj",
    PROJECT / "Program.cs",
    PROJECT / "McpConformanceRunner.cs",
    PROJECT / "McpProcessDriver.cs",
    PROJECT / "Corpus/mcp-conformance-v1.json",
]


class McpConformanceContractTests(unittest.TestCase):
    def test_required_conformance_files_exist(self) -> None:
        for path in REQUIRED:
            self.assertTrue(path.exists(), f"Missing MCP conformance contract: {path}")

    def test_corpus_is_versioned_and_contains_required_categories(self) -> None:
        data = json.loads((PROJECT / "Corpus/mcp-conformance-v1.json").read_text(encoding="utf-8"))
        self.assertEqual("1.0.0", data["schemaVersion"])
        self.assertEqual("2025-11-25", data["protocolVersion"])
        cases = data["cases"]
        ids = [item["id"] for item in cases]
        self.assertEqual(len(ids), len(set(ids)))
        self.assertGreaterEqual(len(cases), 18)
        phases = {item["phase"] for item in cases}
        self.assertEqual({"created", "ready"}, phases)
        joined = "\n".join(ids)
        for category in (
            "parse",
            "root",
            "jsonrpc",
            "method",
            "id",
            "params",
            "unknown",
            "initialize",
        ):
            self.assertIn(category, joined)

    def test_runner_targets_all_five_real_processes(self) -> None:
        runner = (PROJECT / "McpConformanceRunner.cs").read_text(encoding="utf-8")
        for assembly in (
            "BookStudio.Mcp.dll",
            "BookStudio.Mcp.Authoring.dll",
            "BookStudio.Mcp.Quality.dll",
            "BookStudio.Mcp.Production.dll",
            "BookStudio.Mcp.Ops.dll",
        ):
            self.assertIn(assembly, runner)
        for token in (
            "27027",
            "128",
            "SHA256",
            "MaximumMessageBytes",
            "MCP_CONFORMANCE_PASS",
        ):
            self.assertIn(token, runner)

    def test_driver_uses_subprocess_stdio_and_timeouts(self) -> None:
        driver = (PROJECT / "McpProcessDriver.cs").read_text(encoding="utf-8")
        for token in (
            "ProcessStartInfo",
            "RedirectStandardInput",
            "RedirectStandardOutput",
            "RedirectStandardError",
            "ReadLineAsync",
            "CancellationTokenSource",
            "WaitForExitAsync",
        ):
            self.assertIn(token, driver)
        self.assertNotIn("IMcpFeatureRouter", driver)
        self.assertNotIn("McpSession", driver)

    def test_solution_architecture_and_ci_register_conformance(self) -> None:
        solution = (ROOT / "BookStudio.slnx").read_text(encoding="utf-8")
        self.assertIn("tests/BookStudio.Tests.McpConformance/BookStudio.Tests.McpConformance.csproj", solution)

        policy = json.loads((ROOT / "docs/architecture/architecture-policy.json").read_text(encoding="utf-8"))
        names = {project["name"] for project in policy["projects"]}
        self.assertIn("BookStudio.Tests.McpConformance", names)

        providers = json.loads((ROOT / "config/ci/providers.json").read_text(encoding="utf-8"))
        contracts = {item["id"] for item in providers["contracts"]}
        self.assertIn("dotnet.mcp-conformance-integration", contracts)

        workflow = (ROOT / ".github/workflows/02-dotnet-ci.yml").read_text(encoding="utf-8")
        self.assertIn("Run MCP conformance integration journey", workflow)
        self.assertIn("dotnet-mcp-conformance-integration.json", workflow)


if __name__ == "__main__":
    unittest.main()

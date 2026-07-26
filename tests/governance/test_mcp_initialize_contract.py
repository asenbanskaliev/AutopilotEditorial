from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MCP_ROOT = ROOT / "src/BookStudio.Mcp"
REQUIRED = [
    MCP_ROOT / "Protocol/McpProtocolVersions.cs",
    MCP_ROOT / "Protocol/JsonRpcModels.cs",
    MCP_ROOT / "Protocol/McpInitializeModels.cs",
    MCP_ROOT / "Protocol/McpSession.cs",
    MCP_ROOT / "Transport/StdioJsonRpcServer.cs",
    ROOT / "tests/BookStudio.Tests.McpInitialize/BookStudio.Tests.McpInitialize.csproj",
    ROOT / "tests/BookStudio.Tests.McpInitialize/Program.cs",
]


class McpInitializeContractTests(unittest.TestCase):
    def test_required_protocol_and_journey_files_exist(self) -> None:
        for path in REQUIRED:
            self.assertTrue(path.exists(), f"Missing MCP initialize contract: {path}")

    def test_supported_stable_protocol_versions_are_explicit(self) -> None:
        content = (MCP_ROOT / "Protocol/McpProtocolVersions.cs").read_text(encoding="utf-8")
        expected = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"]
        for version in expected:
            self.assertIn(version, content)
        self.assertIn('Latest = "2025-11-25"', content)

    def test_stdio_transport_protects_stdout_and_bounds_messages(self) -> None:
        content = (MCP_ROOT / "Transport/StdioJsonRpcServer.cs").read_text(encoding="utf-8")
        self.assertIn("1_048_576", content)
        self.assertIn("ReadLineAsync", content)
        self.assertIn("WriteLineAsync", content)
        self.assertNotIn("Console.WriteLine", content)

    def test_program_is_protocol_composition_only(self) -> None:
        content = (MCP_ROOT / "Program.cs").read_text(encoding="utf-8")
        self.assertIn("StdioJsonRpcServer", content)
        self.assertNotIn("composition host baseline is ready", content)
        self.assertNotIn("Console.WriteLine", content)

    def test_ci_catalog_contains_mcp_initialize_contract(self) -> None:
        data = json.loads((ROOT / "config/ci/providers.json").read_text(encoding="utf-8"))
        contracts = {item["id"]: item for item in data["contracts"]}
        self.assertIn("dotnet.mcp-initialize-integration", contracts)
        self.assertEqual("integration", contracts["dotnet.mcp-initialize-integration"]["capability"])

    def test_workflow_executes_mcp_initialize_journey(self) -> None:
        workflow = (ROOT / ".github/workflows/02-dotnet-ci.yml").read_text(encoding="utf-8")
        self.assertIn("Run MCP initialize integration journey", workflow)
        self.assertIn("dotnet-mcp-initialize-integration.json", workflow)


if __name__ == "__main__":
    unittest.main()

from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "src/BookStudio.Application"
MCP = ROOT / "src/BookStudio.Mcp.Ops"
REQUIRED = [
    APP / "Operations/IOperationsDiagnosticsService.cs",
    APP / "Operations/OperationsDiagnosticsModels.cs",
    APP / "Operations/OperationsDiagnosticsService.cs",
    MCP / "BookStudio.Mcp.Ops.csproj",
    MCP / "Program.cs",
    MCP / "BookOpsRuntime.cs",
    MCP / "BookOpsToolCatalog.cs",
    MCP / "BookOpsSchemas.cs",
    MCP / "BookOpsFeatureRouter.cs",
    ROOT / "tests/BookStudio.Tests.BookOps/BookStudio.Tests.BookOps.csproj",
    ROOT / "tests/BookStudio.Tests.BookOps/Program.cs",
]


class BookOpsContractTests(unittest.TestCase):
    def test_required_ops_files_exist(self) -> None:
        for path in REQUIRED:
            self.assertTrue(path.exists(), f"Missing book-ops contract: {path}")

    def test_catalog_has_exact_active_and_reserved_surface(self) -> None:
        catalog = (MCP / "BookOpsToolCatalog.cs").read_text(encoding="utf-8")
        for active in ("book.ops.status", "book.ops.diagnostics"):
            self.assertIn(active, catalog)
        for reserved in (
            "book.autopilot.start",
            "book.autopilot.status",
            "book.autopilot.pause",
            "book.autopilot.resume",
            "book.autopilot.cancel",
            "book.autopilot.replay",
        ):
            self.assertIn(reserved, catalog)
        self.assertIn("ActiveTools", catalog)
        self.assertIn("ReservedToolNames", catalog)

    def test_ops_tools_are_read_only_and_structured(self) -> None:
        schemas = (MCP / "BookOpsSchemas.cs").read_text(encoding="utf-8")
        for token in (
            '"inputSchema"',
            '"outputSchema"',
            '"structuredContent"',
            '"readOnlyHint"',
            '"destructiveHint"',
            '"idempotentHint"',
            '"openWorldHint"',
            '"taskSupport"',
            '"forbidden"',
        ):
            self.assertIn(token, schemas)
        self.assertIn("StatusTool", schemas)
        self.assertIn("DiagnosticsTool", schemas)

    def test_application_service_is_provider_neutral(self) -> None:
        service = (APP / "Operations/OperationsDiagnosticsService.cs").read_text(encoding="utf-8")
        self.assertIn("IReadinessProbe", service)
        self.assertNotIn("BookStudio.Infrastructure", service)
        self.assertNotIn("SqliteWorkspaceDatabase", service)

    def test_ops_is_a_separate_mcp_process(self) -> None:
        project = (MCP / "BookStudio.Mcp.Ops.csproj").read_text(encoding="utf-8")
        program = (MCP / "Program.cs").read_text(encoding="utf-8")
        self.assertIn("<OutputType>Exe</OutputType>", project)
        self.assertIn("BookStudio.Mcp", project)
        self.assertIn("bookstudio-ops", program)
        self.assertNotIn("Console.WriteLine", program)

    def test_ci_catalog_contains_ops_contract(self) -> None:
        data = json.loads((ROOT / "config/ci/providers.json").read_text(encoding="utf-8"))
        contracts = {item["id"]: item for item in data["contracts"]}
        self.assertIn("dotnet.book-ops-integration", contracts)
        self.assertEqual("integration", contracts["dotnet.book-ops-integration"]["capability"])

    def test_workflow_executes_ops_journey(self) -> None:
        workflow = (ROOT / ".github/workflows/02-dotnet-ci.yml").read_text(encoding="utf-8")
        self.assertIn("Run book-ops integration journey", workflow)
        self.assertIn("dotnet-book-ops-integration.json", workflow)


if __name__ == "__main__":
    unittest.main()

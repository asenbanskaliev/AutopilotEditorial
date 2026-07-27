from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "src/BookStudio.Application"
MCP = ROOT / "src/BookStudio.Mcp.Production"
REQUIRED = [
    APP / "Production/IReleaseProductionService.cs",
    APP / "Production/ReleaseProductionModels.cs",
    APP / "Production/ReleaseProductionService.cs",
    MCP / "BookStudio.Mcp.Production.csproj",
    MCP / "Program.cs",
    MCP / "BookProductionRuntime.cs",
    MCP / "BookProductionToolCatalog.cs",
    MCP / "BookProductionSchemas.cs",
    MCP / "BookProductionFeatureRouter.cs",
    ROOT / "tests/BookStudio.Tests.BookProduction/BookStudio.Tests.BookProduction.csproj",
    ROOT / "tests/BookStudio.Tests.BookProduction/Program.cs",
]


class BookProductionContractTests(unittest.TestCase):
    def test_required_production_files_exist(self) -> None:
        for path in REQUIRED:
            self.assertTrue(path.exists(), f"Missing book-production contract: {path}")

    def test_catalog_has_exact_active_and_reserved_surface(self) -> None:
        catalog = (MCP / "BookProductionToolCatalog.cs").read_text(encoding="utf-8")
        for active in ("book.release.prepare", "book.preflight.run"):
            self.assertIn(active, catalog)
        for reserved in (
            "book.asset.register",
            "book.render.preview",
            "book.render.final",
            "book.publish.package",
        ):
            self.assertIn(reserved, catalog)
        self.assertIn("ActiveTools", catalog)
        self.assertIn("ReservedToolNames", catalog)

    def test_prepare_and_preflight_annotations_are_distinct(self) -> None:
        schemas = (MCP / "BookProductionSchemas.cs").read_text(encoding="utf-8")
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
        self.assertIn("PrepareTool", schemas)
        self.assertIn("PreflightTool", schemas)

    def test_application_service_is_provider_neutral(self) -> None:
        service = (APP / "Production/ReleaseProductionService.cs").read_text(encoding="utf-8")
        self.assertIn("IArtifactStore", service)
        self.assertNotIn("BookStudio.Infrastructure", service)
        self.assertNotIn("FileArtifactStore", service)

    def test_production_is_a_separate_mcp_process(self) -> None:
        project = (MCP / "BookStudio.Mcp.Production.csproj").read_text(encoding="utf-8")
        program = (MCP / "Program.cs").read_text(encoding="utf-8")
        self.assertIn("<OutputType>Exe</OutputType>", project)
        self.assertIn("BookStudio.Mcp", project)
        self.assertIn("bookstudio-production", program)
        self.assertNotIn("Console.WriteLine", program)

    def test_ci_catalog_contains_production_contract(self) -> None:
        data = json.loads((ROOT / "config/ci/providers.json").read_text(encoding="utf-8"))
        contracts = {item["id"]: item for item in data["contracts"]}
        self.assertIn("dotnet.book-production-integration", contracts)
        self.assertEqual("integration", contracts["dotnet.book-production-integration"]["capability"])

    def test_workflow_executes_production_journey(self) -> None:
        workflow = (ROOT / ".github/workflows/02-dotnet-ci.yml").read_text(encoding="utf-8")
        self.assertIn("Run book-production integration journey", workflow)
        self.assertIn("dotnet-book-production-integration.json", workflow)


if __name__ == "__main__":
    unittest.main()

from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "src/BookStudio.Application"
MCP = ROOT / "src/BookStudio.Mcp.Quality"
REQUIRED = [
    APP / "Quality/IQualityAssessmentService.cs",
    APP / "Quality/QualityAssessmentModels.cs",
    APP / "Quality/QualityAssessmentService.cs",
    MCP / "BookStudio.Mcp.Quality.csproj",
    MCP / "Program.cs",
    MCP / "BookQualityRuntime.cs",
    MCP / "BookQualityToolCatalog.cs",
    MCP / "BookQualitySchemas.cs",
    MCP / "BookQualityFeatureRouter.cs",
    ROOT / "tests/BookStudio.Tests.BookQuality/BookStudio.Tests.BookQuality.csproj",
    ROOT / "tests/BookStudio.Tests.BookQuality/Program.cs",
]


class BookQualityContractTests(unittest.TestCase):
    def test_required_quality_files_exist(self) -> None:
        for path in REQUIRED:
            self.assertTrue(path.exists(), f"Missing book-quality contract: {path}")

    def test_catalog_has_exact_active_and_reserved_surface(self) -> None:
        catalog = (MCP / "BookQualityToolCatalog.cs").read_text(encoding="utf-8")
        for active in ("book.audit.run", "book.gate.evaluate"):
            self.assertIn(active, catalog)
        for reserved in (
            "book.repair.propose",
            "book.repair.apply",
            "book.memory.get",
            "book.memory.commit",
        ):
            self.assertIn(reserved, catalog)
        self.assertIn("ActiveTools", catalog)
        self.assertIn("ReservedToolNames", catalog)

    def test_quality_tools_are_read_only_and_structured(self) -> None:
        schemas = (MCP / "BookQualitySchemas.cs").read_text(encoding="utf-8")
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
        self.assertIn("AuditTool", schemas)
        self.assertIn("GateTool", schemas)

    def test_application_service_is_provider_neutral(self) -> None:
        service = (APP / "Quality/QualityAssessmentService.cs").read_text(encoding="utf-8")
        self.assertIn("IArtifactStore", service)
        self.assertNotIn("BookStudio.Infrastructure", service)
        self.assertNotIn("FileArtifactStore", service)

    def test_quality_is_a_separate_mcp_process(self) -> None:
        project = (MCP / "BookStudio.Mcp.Quality.csproj").read_text(encoding="utf-8")
        program = (MCP / "Program.cs").read_text(encoding="utf-8")
        self.assertIn("<OutputType>Exe</OutputType>", project)
        self.assertIn("BookStudio.Mcp", project)
        self.assertIn("bookstudio-quality", program)
        self.assertNotIn("Console.WriteLine", program)

    def test_ci_catalog_contains_quality_contract(self) -> None:
        data = json.loads((ROOT / "config/ci/providers.json").read_text(encoding="utf-8"))
        contracts = {item["id"]: item for item in data["contracts"]}
        self.assertIn("dotnet.book-quality-integration", contracts)
        self.assertEqual("integration", contracts["dotnet.book-quality-integration"]["capability"])

    def test_workflow_executes_quality_journey(self) -> None:
        workflow = (ROOT / ".github/workflows/02-dotnet-ci.yml").read_text(encoding="utf-8")
        self.assertIn("Run book-quality integration journey", workflow)
        self.assertIn("dotnet-book-quality-integration.json", workflow)


if __name__ == "__main__":
    unittest.main()

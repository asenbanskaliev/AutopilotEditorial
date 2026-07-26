from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "src/BookStudio.Application"
MCP = ROOT / "src/BookStudio.Mcp.Authoring"
REQUIRED = [
    APP / "Authoring/IDraftAuthoringService.cs",
    APP / "Authoring/DraftAuthoringModels.cs",
    APP / "Authoring/DraftAuthoringService.cs",
    MCP / "BookStudio.Mcp.Authoring.csproj",
    MCP / "Program.cs",
    MCP / "BookAuthoringRuntime.cs",
    MCP / "BookAuthoringToolCatalog.cs",
    MCP / "BookAuthoringSchemas.cs",
    MCP / "BookAuthoringFeatureRouter.cs",
    ROOT / "tests/BookStudio.Tests.BookAuthoring/BookStudio.Tests.BookAuthoring.csproj",
    ROOT / "tests/BookStudio.Tests.BookAuthoring/Program.cs",
]


class BookAuthoringContractTests(unittest.TestCase):
    def test_required_authoring_files_exist(self) -> None:
        for path in REQUIRED:
            self.assertTrue(path.exists(), f"Missing book-authoring contract: {path}")

    def test_catalog_has_exact_active_and_reserved_surface(self) -> None:
        catalog = (MCP / "BookAuthoringToolCatalog.cs").read_text(encoding="utf-8")
        for active in ("book.draft.register", "book.draft.validate"):
            self.assertIn(active, catalog)
        for reserved in (
            "book.plan.create",
            "book.scene.generate",
            "book.chapter.generate",
            "book.manuscript.assemble",
        ):
            self.assertIn(reserved, catalog)
        self.assertIn("ActiveTools", catalog)
        self.assertIn("ReservedToolNames", catalog)

    def test_register_and_validate_annotations_are_distinct(self) -> None:
        schemas = (MCP / "BookAuthoringSchemas.cs").read_text(encoding="utf-8")
        for token in (
            '"inputSchema"',
            '"outputSchema"',
            '"structuredContent"',
            '"taskSupport"',
            '"forbidden"',
            '"readOnlyHint"',
            '"destructiveHint"',
            '"idempotentHint"',
            '"openWorldHint"',
        ):
            self.assertIn(token, schemas)
        self.assertIn("RegisterTool", schemas)
        self.assertIn("ValidateTool", schemas)

    def test_application_service_is_provider_neutral(self) -> None:
        service = (APP / "Authoring/DraftAuthoringService.cs").read_text(encoding="utf-8")
        self.assertIn("IArtifactStore", service)
        self.assertNotIn("BookStudio.Infrastructure", service)
        self.assertNotIn("FileArtifactStore", service)

    def test_authoring_is_a_separate_mcp_process(self) -> None:
        project = (MCP / "BookStudio.Mcp.Authoring.csproj").read_text(encoding="utf-8")
        program = (MCP / "Program.cs").read_text(encoding="utf-8")
        self.assertIn("<OutputType>Exe</OutputType>", project)
        self.assertIn("BookStudio.Mcp", project)
        self.assertIn("bookstudio-authoring", program)
        self.assertNotIn("Console.WriteLine", program)

    def test_ci_catalog_contains_authoring_contract(self) -> None:
        data = json.loads((ROOT / "config/ci/providers.json").read_text(encoding="utf-8"))
        contracts = {item["id"]: item for item in data["contracts"]}
        self.assertIn("dotnet.book-authoring-integration", contracts)
        self.assertEqual("integration", contracts["dotnet.book-authoring-integration"]["capability"])

    def test_workflow_executes_authoring_journey(self) -> None:
        workflow = (ROOT / ".github/workflows/02-dotnet-ci.yml").read_text(encoding="utf-8")
        self.assertIn("Run book-authoring integration journey", workflow)
        self.assertIn("dotnet-book-authoring-integration.json", workflow)


if __name__ == "__main__":
    unittest.main()

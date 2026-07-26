from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MCP = ROOT / "src/BookStudio.Mcp"
APP = ROOT / "src/BookStudio.Application"
REQUIRED = [
    APP / "Artifacts/IArtifactQueryService.cs",
    APP / "Artifacts/ArtifactQueryService.cs",
    APP / "Artifacts/ArtifactQueryModels.cs",
    MCP / "Protocol/IMcpFeatureRouter.cs",
    MCP / "BookCore/BookCoreToolCatalog.cs",
    MCP / "BookCore/BookCoreSchemas.cs",
    MCP / "BookCore/BookCoreFeatureRouter.cs",
    MCP / "BookCore/McpCursorCodec.cs",
    MCP / "McpHostOptions.cs",
    ROOT / "tests/BookStudio.Tests.BookCore/BookStudio.Tests.BookCore.csproj",
    ROOT / "tests/BookStudio.Tests.BookCore/Program.cs",
]


class BookCoreContractTests(unittest.TestCase):
    def test_required_book_core_files_exist(self) -> None:
        for path in REQUIRED:
            self.assertTrue(path.exists(), f"Missing book-core contract: {path}")

    def test_only_real_artifact_tools_are_active(self) -> None:
        catalog = (MCP / "BookCore/BookCoreToolCatalog.cs").read_text(encoding="utf-8")
        self.assertIn('"book.artifact.get"', catalog)
        self.assertIn('"book.artifact.compare"', catalog)
        for reserved in (
            "book.project.create",
            "book.project.get_status",
            "book.project.configure",
            "book.decision.submit",
        ):
            self.assertIn(reserved, catalog)
        self.assertIn("ReservedToolNames", catalog)
        self.assertIn("ActiveTools", catalog)

    def test_schemas_include_structured_output_and_annotations(self) -> None:
        content = (MCP / "BookCore/BookCoreSchemas.cs").read_text(encoding="utf-8")
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
            self.assertIn(token, content)

    def test_feature_router_exposes_tools_and_resources_methods(self) -> None:
        content = (MCP / "BookCore/BookCoreFeatureRouter.cs").read_text(encoding="utf-8")
        for method in (
            '"tools/list"',
            '"tools/call"',
            '"resources/list"',
            '"resources/templates/list"',
            '"resources/read"',
        ):
            self.assertIn(method, content)
        self.assertNotIn("book.project.create\" =>", content)
        self.assertNotIn("book.decision.submit\" =>", content)

    def test_application_service_has_no_infrastructure_dependency(self) -> None:
        service = (APP / "Artifacts/ArtifactQueryService.cs").read_text(encoding="utf-8")
        self.assertIn("IArtifactStore", service)
        self.assertNotIn("BookStudio.Infrastructure", service)
        self.assertNotIn("FileArtifactStore", service)

    def test_ci_catalog_contains_book_core_contract(self) -> None:
        data = json.loads((ROOT / "config/ci/providers.json").read_text(encoding="utf-8"))
        contracts = {item["id"]: item for item in data["contracts"]}
        self.assertIn("dotnet.book-core-integration", contracts)
        self.assertEqual("integration", contracts["dotnet.book-core-integration"]["capability"])

    def test_workflow_executes_book_core_journey(self) -> None:
        workflow = (ROOT / ".github/workflows/02-dotnet-ci.yml").read_text(encoding="utf-8")
        self.assertIn("Run book-core integration journey", workflow)
        self.assertIn("dotnet-book-core-integration.json", workflow)


if __name__ == "__main__":
    unittest.main()

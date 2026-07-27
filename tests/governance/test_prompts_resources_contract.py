from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MCP = ROOT / "src/BookStudio.Mcp"
SHARED = [
    MCP / "Prompts/McpPromptModels.cs",
    MCP / "Prompts/VersionedMcpPrompt.cs",
    MCP / "Prompts/VersionedMcpPromptCatalog.cs",
    MCP / "Prompts/McpPromptDispatcher.cs",
    MCP / "Prompts/PromptArgumentRules.cs",
    MCP / "Prompts/PromptEnabledFeatureRouter.cs",
]
SERVER_CONTRACTS = {
    "BookStudio.Mcp": ("BookCore/BookCorePromptCatalog.cs", "Program.cs", "BookCorePromptCatalog", "book.core.inspect-artifact.v1", "book://prompts/book-core/inspect-artifact/v1"),
    "BookStudio.Mcp.Authoring": ("BookAuthoringPromptCatalog.cs", "Program.cs", "BookAuthoringPromptCatalog", "book.authoring.validate-draft.v1", "book://prompts/book-authoring/validate-draft/v1"),
    "BookStudio.Mcp.Quality": ("BookQualityPromptCatalog.cs", "Program.cs", "BookQualityPromptCatalog", "book.quality.assess-draft.v1", "book://prompts/book-quality/assess-draft/v1"),
    "BookStudio.Mcp.Production": ("BookProductionPromptCatalog.cs", "Program.cs", "BookProductionPromptCatalog", "book.production.preflight-release.v1", "book://prompts/book-production/preflight-release/v1"),
    "BookStudio.Mcp.Ops": ("BookOpsPromptCatalog.cs", "Program.cs", "BookOpsPromptCatalog", "book.ops.inspect-readiness.v1", "book://prompts/book-ops/inspect-readiness/v1"),
}


class PromptsResourcesContractTests(unittest.TestCase):
    def test_shared_prompt_protocol_files_exist(self) -> None:
        for path in SHARED:
            self.assertTrue(path.exists(), f"Missing shared prompt contract: {path}")

    def test_each_bounded_server_has_versioned_prompt_catalog(self) -> None:
        for project, (relative, _, _, prompt_name, resource_uri) in SERVER_CONTRACTS.items():
            path = ROOT / "src" / project / relative
            self.assertTrue(path.exists(), f"Missing prompt catalog: {path}")
            content = path.read_text(encoding="utf-8")
            self.assertIn(prompt_name, content)
            self.assertIn(resource_uri, content)
            self.assertIn("VersionedMcpPromptCatalog", content)

    def test_shared_decorator_dispatches_prompts_and_merges_resources(self) -> None:
        content = (MCP / "Prompts/PromptEnabledFeatureRouter.cs").read_text(encoding="utf-8")
        for token in (
            '"prompts"',
            "McpPromptDispatcher.TryDispatch",
            "Concat(prompts.Resources)",
            "HandlePromptResourceRead",
            "MergeCapabilities",
            "McpCursorCodec",
        ):
            self.assertIn(token, content)

    def test_all_composition_roots_enable_matching_prompt_catalog(self) -> None:
        for project, (_, program_relative, catalog_name, _, _) in SERVER_CONTRACTS.items():
            program = ROOT / "src" / project / program_relative
            content = program.read_text(encoding="utf-8")
            self.assertIn("PromptEnabledFeatureRouter", content)
            self.assertIn(catalog_name + ".Catalog", content)

    def test_prompt_dispatcher_has_strict_list_get_contract(self) -> None:
        content = (MCP / "Prompts/McpPromptDispatcher.cs").read_text(encoding="utf-8")
        for token in (
            "prompts/list",
            "prompts/get",
            "InvalidParams",
            "McpCursorCodec",
            "arguments",
            "McpPromptMessage",
            "McpTextContent",
        ):
            self.assertIn(token, content)

    def test_integration_project_and_ci_contract_exist(self) -> None:
        project = ROOT / "tests/BookStudio.Tests.PromptsResources/BookStudio.Tests.PromptsResources.csproj"
        program = ROOT / "tests/BookStudio.Tests.PromptsResources/Program.cs"
        self.assertTrue(project.exists())
        self.assertTrue(program.exists())
        data = json.loads((ROOT / "config/ci/providers.json").read_text(encoding="utf-8"))
        contracts = {item["id"]: item for item in data["contracts"]}
        self.assertIn("dotnet.prompts-resources-integration", contracts)
        self.assertEqual("integration", contracts["dotnet.prompts-resources-integration"]["capability"])

    def test_workflow_executes_prompt_conformance_journey(self) -> None:
        workflow = (ROOT / ".github/workflows/02-dotnet-ci.yml").read_text(encoding="utf-8")
        self.assertIn("Run prompts-resources integration journey", workflow)
        self.assertIn("dotnet-prompts-resources-integration.json", workflow)


if __name__ == "__main__":
    unittest.main()

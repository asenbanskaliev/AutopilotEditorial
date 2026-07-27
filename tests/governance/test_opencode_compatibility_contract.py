from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APPLICATION = ROOT / "src/BookStudio.Application/OpenCode"
ADAPTER = ROOT / "src/BookStudio.OpenCode"
TEST_PROJECT = ROOT / "tests/BookStudio.Tests.OpenCodeCompatibility"

REQUIRED = [
    APPLICATION / "IOpenCodeCompatibilityProbe.cs",
    APPLICATION / "OpenCodeCompatibilityReport.cs",
    APPLICATION / "OpenCodeFeatureIds.cs",
    ADAPTER / "OpenCodeEndpointOptions.cs",
    ADAPTER / "OpenCodeOpenApiInspector.cs",
    ADAPTER / "OpenCodeCompatibilityProbe.cs",
    TEST_PROJECT / "AGENTS.md",
    TEST_PROJECT / "BookStudio.Tests.OpenCodeCompatibility.csproj",
    TEST_PROJECT / "Program.cs",
    TEST_PROJECT / "ContractualOpenCodeServer.cs",
]


class OpenCodeCompatibilityContractTests(unittest.TestCase):
    def test_required_contract_files_exist(self) -> None:
        for path in REQUIRED:
            self.assertTrue(path.exists(), f"Missing OpenCode compatibility contract: {path}")

    def test_application_contract_is_provider_neutral(self) -> None:
        for path in (
            APPLICATION / "IOpenCodeCompatibilityProbe.cs",
            APPLICATION / "OpenCodeCompatibilityReport.cs",
            APPLICATION / "OpenCodeFeatureIds.cs",
        ):
            content = path.read_text(encoding="utf-8")
            self.assertNotIn("HttpClient", content)
            self.assertNotIn("HttpRequestMessage", content)
            self.assertNotIn("System.Text.Json", content)
            self.assertNotIn("Uri", content)
        catalog = (APPLICATION / "OpenCodeFeatureIds.cs").read_text(encoding="utf-8")
        for feature in (
            "health",
            "providers.list",
            "agents.list",
            "mcp.status",
            "sessions.list",
            "sessions.create",
            "sessions.get",
            "sessions.status",
            "sessions.prompt_async",
            "sessions.abort",
            "events.project",
            "events.global",
        ):
            self.assertIn(feature, catalog)

    def test_adapter_enforces_health_openapi_auth_and_bounds(self) -> None:
        options = (ADAPTER / "OpenCodeEndpointOptions.cs").read_text(encoding="utf-8")
        for token in (
            "Uri.UriSchemeHttp",
            "Uri.UriSchemeHttps",
            "IsLoopback",
            "MaximumHealthBytes",
            "MaximumSpecificationBytes",
            "RequestTimeout",
            "Username",
            "Password",
        ):
            self.assertIn(token, options)

        probe = (ADAPTER / "OpenCodeCompatibilityProbe.cs").read_text(encoding="utf-8")
        for token in (
            '"global/health"',
            '"doc"',
            "HttpMethod.Get",
            "Basic",
            "ResponseHeadersRead",
            "CancellationTokenSource",
            "OpenCodeOpenApiInspector",
        ):
            self.assertIn(token, probe)
        for forbidden in (
            "HttpMethod.Post",
            "HttpMethod.Put",
            "HttpMethod.Patch",
            "HttpMethod.Delete",
        ):
            self.assertNotIn(forbidden, probe)

    def test_openapi_inspector_requires_31_and_required_operations(self) -> None:
        inspector = (ADAPTER / "OpenCodeOpenApiInspector.cs").read_text(encoding="utf-8")
        for token in (
            'StartsWith("3."',
            '"paths"',
            '"get"',
            '"post"',
            '"/session/{id}/prompt_async"',
            '"/session/{id}/abort"',
            '"/global/event"',
        ):
            self.assertIn(token, inspector)

    def test_integration_and_ci_registration_exist(self) -> None:
        solution = (ROOT / "BookStudio.slnx").read_text(encoding="utf-8")
        self.assertIn(
            "tests/BookStudio.Tests.OpenCodeCompatibility/BookStudio.Tests.OpenCodeCompatibility.csproj",
            solution,
        )

        policy = json.loads(
            (ROOT / "docs/architecture/architecture-policy.json").read_text(encoding="utf-8")
        )
        names = {project["name"] for project in policy["projects"]}
        self.assertIn("BookStudio.Tests.OpenCodeCompatibility", names)

        providers = json.loads(
            (ROOT / "config/ci/providers.json").read_text(encoding="utf-8")
        )
        contracts = {item["id"] for item in providers["contracts"]}
        self.assertIn("dotnet.opencode-compatibility-integration", contracts)

        workflow = (ROOT / ".github/workflows/02-dotnet-ci.yml").read_text(encoding="utf-8")
        self.assertIn("Run OpenCode compatibility integration journey", workflow)
        self.assertIn("dotnet-opencode-compatibility-integration.json", workflow)


if __name__ == "__main__":
    unittest.main()

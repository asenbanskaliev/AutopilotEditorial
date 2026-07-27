from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MCP = ROOT / "src/BookStudio.Mcp"
INFRA = ROOT / "src/BookStudio.Infrastructure/Artifacts/FileSystem"
APP = ROOT / "src/BookStudio.Application/Artifacts/IArtifactStore.cs"
TEST = ROOT / "tests/BookStudio.Tests.McpSecuritySandbox"
REQUIRED = [
    MCP / "Security/McpWorkspaceSandboxPolicy.cs",
    MCP / "Security/McpSandboxPolicyResource.cs",
    MCP / "Security/SandboxEnabledFeatureRouter.cs",
    TEST / "AGENTS.md",
    TEST / "BookStudio.Tests.McpSecuritySandbox.csproj",
    TEST / "Program.cs",
    TEST / "SandboxProcessDriver.cs",
]


class McpSecuritySandboxContractTests(unittest.TestCase):
    def test_required_security_components_exist(self) -> None:
        for path in REQUIRED:
            self.assertTrue(path.exists(), f"Missing MCP security sandbox component: {path}")

    def test_host_options_define_strict_limits(self) -> None:
        content = (MCP / "McpHostOptions.cs").read_text(encoding="utf-8")
        for token in (
            "MaximumArtifactBytes",
            "MaximumStoreBytes",
            "MaximumStoreFiles",
            "16L * 1024L * 1024L",
            "1024L * 1024L * 1024L",
            "100000",
            "--max-artifact-bytes",
            "--max-store-bytes",
            "--max-store-files",
            "McpWorkspaceSandboxPolicy",
        ):
            self.assertIn(token, content)

    def test_artifact_store_has_global_quota_contract(self) -> None:
        options = (INFRA / "FileArtifactStoreOptions.cs").read_text(encoding="utf-8")
        store = (INFRA / "FileArtifactStore.cs").read_text(encoding="utf-8")
        application = APP.read_text(encoding="utf-8")
        for token in ("MaximumStoreBytes", "MaximumStoreFiles"):
            self.assertIn(token, options)
        for token in (
            "ArtifactStoreQuotaExceededException",
            "SemaphoreSlim",
            "EnsureWriteQuota",
            "ManifestQuotaReserveBytes",
            "MaximumStoreBytes",
            "MaximumStoreFiles",
        ):
            self.assertIn(token, store)
        self.assertIn("ArtifactStoreQuotaExceededException", application)

    def test_all_mcp_programs_enable_sandbox_policy(self) -> None:
        projects = (
            "BookStudio.Mcp",
            "BookStudio.Mcp.Authoring",
            "BookStudio.Mcp.Quality",
            "BookStudio.Mcp.Production",
            "BookStudio.Mcp.Ops",
        )
        for project in projects:
            program = (ROOT / "src" / project / "Program.cs").read_text(encoding="utf-8")
            self.assertIn("SandboxEnabledFeatureRouter", program)
            self.assertIn("options", program)

    def test_policy_resource_is_shared_and_path_free(self) -> None:
        resource = (MCP / "Security/McpSandboxPolicyResource.cs").read_text(encoding="utf-8")
        for token in (
            "book://security/sandbox-policy",
            "application/vnd.bookstudio.sandbox-policy+json",
            "maximumArtifactBytes",
            "maximumStoreBytes",
            "maximumStoreFiles",
            "strict-local",
        ):
            self.assertIn(token, resource)
        self.assertNotIn("WorkspaceRoot", resource)

    def test_security_integration_and_ci_are_registered(self) -> None:
        self.assertTrue((TEST / "BookStudio.Tests.McpSecuritySandbox.csproj").exists())
        self.assertTrue((TEST / "Program.cs").exists())

        solution = (ROOT / "BookStudio.slnx").read_text(encoding="utf-8")
        self.assertIn("tests/BookStudio.Tests.McpSecuritySandbox/BookStudio.Tests.McpSecuritySandbox.csproj", solution)

        policy = json.loads((ROOT / "docs/architecture/architecture-policy.json").read_text(encoding="utf-8"))
        self.assertIn("BookStudio.Tests.McpSecuritySandbox", {item["name"] for item in policy["projects"]})

        providers = json.loads((ROOT / "config/ci/providers.json").read_text(encoding="utf-8"))
        self.assertIn("dotnet.mcp-security-sandbox-integration", {item["id"] for item in providers["contracts"]})

        workflow = (ROOT / ".github/workflows/02-dotnet-ci.yml").read_text(encoding="utf-8")
        self.assertIn("Run MCP security sandbox integration journey", workflow)
        self.assertIn("dotnet-mcp-security-sandbox-integration.json", workflow)


if __name__ == "__main__":
    unittest.main()

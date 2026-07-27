from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "src/BookStudio.Application/OpenCode"
ADAPTER = ROOT / "src/BookStudio.OpenCode"
TEST_PROJECT = ROOT / "tests/BookStudio.Tests.AgentToolProfiles"
CONFIG = ROOT / "config/opencode"

REQUIRED = [
    APP / "AgentToolProfileContracts.cs",
    APP / "IAgentToolProfileResolver.cs",
    APP / "AgentToolProfileCatalog.cs",
    APP / "AgentToolProfileResolver.cs",
    ADAPTER / "OpenCodeAgentToolProfileCatalogLoader.cs",
    ADAPTER / "OpenCodeAgentToolProfileMapper.cs",
    CONFIG / "agent-tool-profiles.json",
    CONFIG / "agent-tool-profiles.schema.json",
    TEST_PROJECT / "AGENTS.md",
    TEST_PROJECT / "BookStudio.Tests.AgentToolProfiles.csproj",
    TEST_PROJECT / "Program.cs",
    TEST_PROJECT / "AgentToolProfilesJourney.cs",
]


class AgentToolProfilesContractTests(unittest.TestCase):
    def test_required_contract_files_exist(self) -> None:
        for path in REQUIRED:
            self.assertTrue(path.exists(), f"Missing agent-tool-profile contract: {path}")

    def test_application_contract_is_provider_neutral(self) -> None:
        for name in (
            "AgentToolProfileContracts.cs",
            "IAgentToolProfileResolver.cs",
            "AgentToolProfileCatalog.cs",
            "AgentToolProfileResolver.cs",
        ):
            content = (APP / name).read_text(encoding="utf-8")
            for forbidden in (
                "HttpClient",
                "HttpRequestMessage",
                "HttpStatusCode",
                "System.Text.Json",
                "JsonElement",
                "OpenCodeAgent",
                "Authorization",
                "ProcessStartInfo",
                "Directory.GetFiles",
            ):
                self.assertNotIn(forbidden, content)

    def test_profile_schema_and_stable_errors_exist(self) -> None:
        contracts = (APP / "AgentToolProfileContracts.cs").read_text(encoding="utf-8")
        for token in (
            "AgentToolProfileDefinition",
            "AgentToolProfileResolutionRequest",
            "EffectiveAgentToolProfile",
            "AgentToolProfileProductLimits",
            "AgentToolProfileCapabilities",
            "AgentToolProfileTools",
            "agent_profile_invalid",
            "agent_profile_not_found",
            "agent_profile_version_not_found",
            "agent_profile_workflow_mismatch",
            "agent_profile_role_mismatch",
            "agent_profile_unknown_capability",
            "agent_profile_unknown_tool",
            "agent_profile_permission_denied",
            "agent_profile_privilege_escalation",
            "agent_profile_provider_unsupported",
            "agent_profile_limits_invalid",
        ):
            self.assertIn(token, contracts)

        schema = json.loads((CONFIG / "agent-tool-profiles.schema.json").read_text(encoding="utf-8"))
        self.assertEqual("https://json-schema.org/draft/2020-12/schema", schema["$schema"])
        self.assertFalse(schema["additionalProperties"])

        catalog = json.loads((CONFIG / "agent-tool-profiles.json").read_text(encoding="utf-8"))
        self.assertGreaterEqual(catalog["catalogVersion"], 1)
        self.assertGreaterEqual(len(catalog["profiles"]), 5)

    def test_resolver_is_deterministic_deny_by_default_and_escalation_safe(self) -> None:
        resolver = (APP / "AgentToolProfileResolver.cs").read_text(encoding="utf-8")
        for token in (
            "IAgentToolProfileResolver",
            "AgentToolProfileFingerprint",
            "SHA256",
            "StringComparer.Ordinal",
            "PermissionDenied",
            "PrivilegeEscalation",
            "RequiresHumanApproval",
            "MaximumToolCalls",
            "MaximumParallelTools",
            "Parent",
            "IsSubsetOf",
            "Math.Min",
        ):
            self.assertIn(token, resolver)
        for forbidden in ("Random", "Guid.NewGuid", "DateTime", "DateTimeOffset"):
            self.assertNotIn(forbidden, resolver)

    def test_catalog_is_immutable_versioned_and_bounded(self) -> None:
        catalog = (APP / "AgentToolProfileCatalog.cs").read_text(encoding="utf-8")
        for token in (
            "CatalogVersion",
            "MaximumProfiles",
            "MaximumEntriesPerList",
            "ToArray",
            "ProfileId",
            "Version",
            "StringComparer.Ordinal",
        ):
            self.assertIn(token, catalog)
        self.assertNotIn("ConcurrentDictionary", catalog)

    def test_opencode_loader_and_mapper_fail_closed(self) -> None:
        loader = (ADAPTER / "OpenCodeAgentToolProfileCatalogLoader.cs").read_text(encoding="utf-8")
        for token in (
            "JsonDocumentOptions",
            "MaxDepth",
            "MaximumPayloadBytes",
            "EnsureUniqueProperties",
            "AgentToolProfileCatalog",
        ):
            self.assertIn(token, loader)
        self.assertNotIn("ReadToEndAsync", loader)

        mapper = (ADAPTER / "OpenCodeAgentToolProfileMapper.cs").read_text(encoding="utf-8")
        for token in (
            "SupportsDenyByDefault",
            "SupportsExplicitDeny",
            "SupportedTools",
            "DeniedTools",
            "AgentToolProfileFingerprint.Verify",
            "ProviderUnsupported",
        ):
            self.assertIn(token, mapper)
        for forbidden in (
            "HttpClient",
            "HttpMethod.Post",
            "prompt_async",
            "session/",
            "Authorization",
        ):
            self.assertNotIn(forbidden, mapper)

    def test_real_journey_covers_all_security_gates(self) -> None:
        journey = (TEST_PROJECT / "AgentToolProfilesJourney.cs").read_text(encoding="utf-8")
        for token in (
            "RepositoryCatalogAsync",
            "WorkflowResolutionAsync",
            "DenyByDefaultAsync",
            "DenyOverridesAllowAsync",
            "UnknownValuesAsync",
            "DeterministicFingerprintAsync",
            "ExactSelectorsAsync",
            "ChildNarrowingAsync",
            "ApprovalAndLimitsAsync",
            "ProviderMappingAsync",
            "ConcurrentResolutionAsync",
            "NoMutationAndSafeEvidenceAsync",
            "NO_PRIVILEGE_ESCALATION",
            "mutation=NONE",
        ):
            self.assertIn(token, journey)

        program = (TEST_PROJECT / "Program.cs").read_text(encoding="utf-8")
        self.assertIn("OPENCODE_AGENT_TOOL_PROFILES_PASS", program)

    def test_solution_architecture_and_ci_registration_exist(self) -> None:
        project_path = "tests/BookStudio.Tests.AgentToolProfiles/BookStudio.Tests.AgentToolProfiles.csproj"
        self.assertIn(project_path, (ROOT / "BookStudio.slnx").read_text(encoding="utf-8"))

        policy = json.loads((ROOT / "docs/architecture/architecture-policy.json").read_text(encoding="utf-8"))
        projects = {project["name"]: project for project in policy["projects"]}
        self.assertIn("BookStudio.Tests.AgentToolProfiles", projects)
        self.assertEqual(
            ["BookStudio.Application", "BookStudio.OpenCode"],
            projects["BookStudio.Tests.AgentToolProfiles"]["allowedBookStudioAssemblyReferences"],
        )

        providers = json.loads((ROOT / "config/ci/providers.json").read_text(encoding="utf-8"))
        contracts = {item["id"] for item in providers["contracts"]}
        self.assertIn("dotnet.agent-tool-profiles-integration", contracts)

        workflow = (ROOT / ".github/workflows/02-dotnet-ci.yml").read_text(encoding="utf-8")
        self.assertIn("Run agent tool profiles integration journey", workflow)
        self.assertIn("dotnet-agent-tool-profiles-integration.json", workflow)


if __name__ == "__main__":
    unittest.main()

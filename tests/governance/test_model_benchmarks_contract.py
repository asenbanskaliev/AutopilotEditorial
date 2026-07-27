from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "src/BookStudio.Application/OpenCode"
ADAPTER = ROOT / "src/BookStudio.OpenCode"
CONFIG = ROOT / "config/opencode"
TEST_PROJECT = ROOT / "tests/BookStudio.Tests.ModelBenchmarks"

REQUIRED = [
    APP / "ModelBenchmarkContracts.cs",
    APP / "IModelAssignmentSelector.cs",
    APP / "ModelBenchmarkCatalog.cs",
    APP / "ModelAssignmentSelector.cs",
    ADAPTER / "OpenCodeModelBenchmarkCatalogLoader.cs",
    ADAPTER / "OpenCodeModelAssignmentMapper.cs",
    CONFIG / "model-benchmarks.json",
    CONFIG / "model-benchmarks.schema.json",
    TEST_PROJECT / "AGENTS.md",
    TEST_PROJECT / "BookStudio.Tests.ModelBenchmarks.csproj",
    TEST_PROJECT / "Program.cs",
    TEST_PROJECT / "ModelBenchmarksJourney.cs",
]


class ModelBenchmarksContractTests(unittest.TestCase):
    def test_required_files_exist(self) -> None:
        for path in REQUIRED:
            self.assertTrue(path.exists(), f"Missing model benchmark contract: {path}")

    def test_application_is_provider_neutral(self) -> None:
        for name in (
            "ModelBenchmarkContracts.cs",
            "IModelAssignmentSelector.cs",
            "ModelBenchmarkCatalog.cs",
            "ModelAssignmentSelector.cs",
        ):
            content = (APP / name).read_text(encoding="utf-8")
            for forbidden in (
                "HttpClient",
                "HttpRequestMessage",
                "HttpStatusCode",
                "System.Text.Json",
                "JsonElement",
                "Authorization",
                "OpenCodeModel",
                "DateTime.UtcNow",
                "DateTimeOffset.UtcNow",
                "Random",
                "Guid.NewGuid",
            ):
                self.assertNotIn(forbidden, content)

    def test_contracts_include_dimensions_constraints_and_stable_codes(self) -> None:
        content = (APP / "ModelBenchmarkContracts.cs").read_text(encoding="utf-8")
        for token in (
            "ModelBenchmarkDimensions",
            "ModelLocalities",
            "ModelBenchmarkEvidence",
            "ModelBenchmarkDefinition",
            "ModelRolePolicyDefinition",
            "ModelAssignmentRequest",
            "ModelAssignmentDecision",
            "PrimaryModelIds",
            "FallbackModelIds",
            "RequiredDimensions",
            "WeightsBasisPoints",
            "model_benchmark_invalid",
            "model_benchmark_missing_evidence",
            "model_benchmark_stale_evidence",
            "model_benchmark_low_confidence",
            "model_assignment_no_eligible_model",
            "model_assignment_provider_unavailable",
            "model_assignment_profile_fingerprint_invalid",
            "model_assignment_provider_unsupported",
        ):
            self.assertIn(token, content)

    def test_catalog_is_immutable_bounded_and_exact(self) -> None:
        content = (APP / "ModelBenchmarkCatalog.cs").read_text(encoding="utf-8")
        for token in (
            "MaximumModels",
            "MaximumRolePolicies",
            "MaximumEvidenceEntries",
            "MaximumListEntries",
            "StringComparer.Ordinal",
            "ToArray",
            "CatalogVersion",
            "MeasuredAtEpochSeconds",
            "PrimaryModelIds",
            "FallbackModelIds",
        ):
            self.assertIn(token, content)
        self.assertNotIn("ConcurrentDictionary", content)

    def test_selector_enforces_hard_constraints_before_scoring(self) -> None:
        content = (APP / "ModelAssignmentSelector.cs").read_text(encoding="utf-8")
        for token in (
            "IModelAssignmentSelector",
            "EvaluateEligibility",
            "MaximumEvidenceAgeSeconds",
            "MinimumConfidenceBasisPoints",
            "MinimumContextWindowTokens",
            "MaximumInputCostMicrosPerMillion",
            "MaximumOutputCostMicrosPerMillion",
            "MaximumMedianLatencyMs",
            "MinimumSafetyTier",
            "RequiredDimensions",
            "WeightsBasisPoints",
            "PrimaryModelIds",
            "FallbackModelIds",
            "checked",
            "Math.Min",
            "StringComparer.Ordinal",
            "ModelAssignmentFingerprint",
        ):
            self.assertIn(token, content)
        self.assertLess(content.index("EvaluateEligibility"), content.index("CalculateWeightedScore"))

    def test_loader_is_strict_and_bounded(self) -> None:
        content = (ADAPTER / "OpenCodeModelBenchmarkCatalogLoader.cs").read_text(encoding="utf-8")
        for token in (
            "JsonDocumentOptions",
            "MaxDepth",
            "MaximumPayloadBytes",
            "EnsureUniqueProperties",
            "EnsureAllowedProperties",
            "ModelBenchmarkCatalog",
        ):
            self.assertIn(token, content)
        self.assertNotIn("ReadToEndAsync", content)

    def test_provider_mapping_is_pure_and_fail_closed(self) -> None:
        content = (ADAPTER / "OpenCodeModelAssignmentMapper.cs").read_text(encoding="utf-8")
        for token in (
            "ModelAssignmentFingerprint.Verify",
            "AdvertisedModels",
            "ProviderFamily",
            "ProviderModelKey",
            "ProviderUnsupported",
        ):
            self.assertIn(token, content)
        for forbidden in (
            "HttpClient",
            "HttpMethod.Post",
            "prompt_async",
            "session/",
            "Authorization",
        ):
            self.assertNotIn(forbidden, content)

    def test_repository_schema_and_catalog_exist(self) -> None:
        schema = json.loads((CONFIG / "model-benchmarks.schema.json").read_text(encoding="utf-8"))
        self.assertEqual("https://json-schema.org/draft/2020-12/schema", schema["$schema"])
        self.assertFalse(schema["additionalProperties"])

        catalog = json.loads((CONFIG / "model-benchmarks.json").read_text(encoding="utf-8"))
        self.assertGreaterEqual(catalog["catalogVersion"], 1)
        self.assertGreaterEqual(len(catalog["models"]), 5)
        self.assertGreaterEqual(len(catalog["rolePolicies"]), 5)

    def test_real_journey_covers_all_gates(self) -> None:
        journey = (TEST_PROJECT / "ModelBenchmarksJourney.cs").read_text(encoding="utf-8")
        for token in (
            "RepositoryCatalogAsync",
            "RolePolicyVersioningAsync",
            "HardConstraintsAsync",
            "MissingEvidenceAsync",
            "StaleEvidenceAsync",
            "LowConfidenceAsync",
            "DeterministicRankingAsync",
            "TieBreakingAsync",
            "ExplicitFallbackAsync",
            "FallbackCannotBypassAsync",
            "ProviderAvailabilityNarrowsAsync",
            "FingerprintValidationAsync",
            "ProviderMappingAsync",
            "ConcurrencyCancellationNoMutationAsync",
            "HARD_CONSTRAINTS",
            "mutation=NONE",
        ):
            self.assertIn(token, journey)

        program = (TEST_PROJECT / "Program.cs").read_text(encoding="utf-8")
        self.assertIn("OPENCODE_MODEL_BENCHMARKS_PASS", program)

    def test_solution_architecture_and_ci_registration_exist(self) -> None:
        project_path = "tests/BookStudio.Tests.ModelBenchmarks/BookStudio.Tests.ModelBenchmarks.csproj"
        self.assertIn(project_path, (ROOT / "BookStudio.slnx").read_text(encoding="utf-8"))

        policy = json.loads((ROOT / "docs/architecture/architecture-policy.json").read_text(encoding="utf-8"))
        projects = {project["name"]: project for project in policy["projects"]}
        self.assertIn("BookStudio.Tests.ModelBenchmarks", projects)
        self.assertEqual(
            ["BookStudio.Application", "BookStudio.OpenCode"],
            projects["BookStudio.Tests.ModelBenchmarks"]["allowedBookStudioAssemblyReferences"],
        )

        providers = json.loads((ROOT / "config/ci/providers.json").read_text(encoding="utf-8"))
        contracts = {item["id"] for item in providers["contracts"]}
        self.assertIn("dotnet.model-benchmarks-integration", contracts)

        workflow = (ROOT / ".github/workflows/02-dotnet-ci.yml").read_text(encoding="utf-8")
        self.assertIn("Run model benchmarks integration journey", workflow)
        self.assertIn("dotnet-model-benchmarks-integration.json", workflow)


if __name__ == "__main__":
    unittest.main()

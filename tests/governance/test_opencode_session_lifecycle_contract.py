from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APPLICATION = ROOT / "src/BookStudio.Application/OpenCode"
ADAPTER = ROOT / "src/BookStudio.OpenCode"
TEST_PROJECT = ROOT / "tests/BookStudio.Tests.OpenCodeSessionLifecycle"

REQUIRED = [
    APPLICATION / "IOpenCodeSessionLifecycle.cs",
    APPLICATION / "OpenCodeSessionContracts.cs",
    APPLICATION / "OpenCodeSessionValidation.cs",
    ADAPTER / "OpenCodeSessionLifecycleClient.cs",
    ADAPTER / "OpenCodeSessionIdempotencyLedger.cs",
    TEST_PROJECT / "AGENTS.md",
    TEST_PROJECT / "BookStudio.Tests.OpenCodeSessionLifecycle.csproj",
    TEST_PROJECT / "Program.cs",
    TEST_PROJECT / "ContractualOpenCodeSessionServer.cs",
    TEST_PROJECT / "OpenCodeSessionLifecycleJourney.cs",
]


class OpenCodeSessionLifecycleContractTests(unittest.TestCase):
    def test_required_contract_files_exist(self) -> None:
        for path in REQUIRED:
            self.assertTrue(path.exists(), f"Missing OpenCode session lifecycle contract: {path}")

    def test_application_contract_is_provider_neutral_and_complete(self) -> None:
        paths = [
            APPLICATION / "IOpenCodeSessionLifecycle.cs",
            APPLICATION / "OpenCodeSessionContracts.cs",
            APPLICATION / "OpenCodeSessionValidation.cs",
        ]
        for path in paths:
            content = path.read_text(encoding="utf-8")
            for forbidden in (
                "HttpClient",
                "HttpRequestMessage",
                "HttpStatusCode",
                "System.Text.Json",
                "JsonElement",
                "Uri",
                "Authorization",
            ):
                self.assertNotIn(forbidden, content)

        interface = (APPLICATION / "IOpenCodeSessionLifecycle.cs").read_text(encoding="utf-8")
        for method in (
            "CreateSessionAsync",
            "GetSessionAsync",
            "GetStatusesAsync",
            "SendPromptAsync",
            "AbortSessionAsync",
        ):
            self.assertIn(method, interface)

        contracts = (APPLICATION / "OpenCodeSessionContracts.cs").read_text(encoding="utf-8")
        for token in (
            "OpenCodeCreateSessionCommand",
            "OpenCodeSendPromptCommand",
            "OpenCodeTextPart",
            "OpenCodeSession",
            "OpenCodeSessionStatus",
            "OpenCodePromptSubmission",
            "OpenCodeAbortResult",
            "idle",
            "busy",
            "retry",
            "unknown",
            "idempotency_conflict",
        ):
            self.assertIn(token, contracts)

    def test_adapter_is_compatibility_gated_bounded_and_exact(self) -> None:
        client = (ADAPTER / "OpenCodeSessionLifecycleClient.cs").read_text(encoding="utf-8")
        for token in (
            "IOpenCodeCompatibilityProbe",
            "OpenCodeFeatureIds.SessionsCreate",
            "OpenCodeFeatureIds.SessionsGet",
            "OpenCodeFeatureIds.SessionsStatus",
            "OpenCodeFeatureIds.SessionsPromptAsync",
            "OpenCodeFeatureIds.SessionsAbort",
            '"session"',
            '"session/status"',
            '"prompt_async"',
            '"abort"',
            "HttpMethod.Get",
            "HttpMethod.Post",
            "ResponseHeadersRead",
            "MaximumResponseBytes",
            "MaximumRequestBytes",
            "CancellationTokenSource",
            "Basic",
        ):
            self.assertIn(token, client)

        for forbidden in (
            "HttpMethod.Delete",
            "HttpMethod.Patch",
            '"shell"',
            '"command"',
            '"share"',
            '"file"',
        ):
            self.assertNotIn(forbidden, client)

        ledger = (ADAPTER / "OpenCodeSessionIdempotencyLedger.cs").read_text(encoding="utf-8")
        for token in (
            "SHA256",
            "ConcurrentDictionary",
            "idempotency_conflict",
            "idempotency_capacity_exceeded",
            "TryRemove",
        ):
            self.assertIn(token, ledger)

    def test_validation_contract_contains_required_bounds(self) -> None:
        validation = (APPLICATION / "OpenCodeSessionValidation.cs").read_text(encoding="utf-8")
        for token in (
            "MaximumSessionIdBytes",
            "MaximumIdempotencyKeyBytes",
            "MaximumTitleBytes",
            "MaximumPromptPartCount",
            "MaximumTextPartBytes",
            "MaximumPromptBytes",
            "MaximumStatusEntries",
            "ValidateSessionId",
            "ValidateIdempotencyKey",
            "ValidateTextParts",
        ):
            self.assertIn(token, validation)

    def test_real_journey_covers_lifecycle_idempotency_and_no_extra_mutation(self) -> None:
        journey = (TEST_PROJECT / "OpenCodeSessionLifecycleJourney.cs").read_text(encoding="utf-8")
        for token in (
            "CompatibilityRefusalAsync",
            "CreateAndGetAsync",
            "CreateIdempotencyAsync",
            "ConcurrentCreateIdempotencyAsync",
            "PromptAsync",
            "PromptIdempotencyAsync",
            "StatusesAsync",
            "AbortAsync",
            "AuthenticationAsync",
            "BoundsAsync",
            "MalformedResponsesAsync",
            "TimeoutAndCancellationAsync",
            "FailedReservationCanRetryAsync",
            "NO_UNPLANNED_MUTATION",
            "SESSION_LIFECYCLE_PASS",
        ):
            self.assertIn(token, journey)

        server = (TEST_PROJECT / "ContractualOpenCodeSessionServer.cs").read_text(encoding="utf-8")
        for token in (
            "TcpListener",
            "Authorization",
            "Content-Length",
            "Requests",
        ):
            self.assertIn(token, server)

    def test_solution_architecture_and_ci_registration_exist(self) -> None:
        project_path = "tests/BookStudio.Tests.OpenCodeSessionLifecycle/BookStudio.Tests.OpenCodeSessionLifecycle.csproj"
        solution = (ROOT / "BookStudio.slnx").read_text(encoding="utf-8")
        self.assertIn(project_path, solution)

        policy = json.loads(
            (ROOT / "docs/architecture/architecture-policy.json").read_text(encoding="utf-8")
        )
        projects = {project["name"]: project for project in policy["projects"]}
        self.assertIn("BookStudio.Tests.OpenCodeSessionLifecycle", projects)
        self.assertEqual(
            projects["BookStudio.Tests.OpenCodeSessionLifecycle"]["allowedBookStudioAssemblyReferences"],
            ["BookStudio.Application", "BookStudio.OpenCode"],
        )

        providers = json.loads(
            (ROOT / "config/ci/providers.json").read_text(encoding="utf-8")
        )
        contracts = {item["id"] for item in providers["contracts"]}
        self.assertIn("dotnet.opencode-session-lifecycle-integration", contracts)

        workflow = (ROOT / ".github/workflows/02-dotnet-ci.yml").read_text(encoding="utf-8")
        self.assertIn("Run OpenCode session lifecycle integration journey", workflow)
        self.assertIn("dotnet-opencode-session-lifecycle-integration.json", workflow)


if __name__ == "__main__":
    unittest.main()

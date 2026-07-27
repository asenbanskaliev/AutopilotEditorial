from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APPLICATION = ROOT / "src/BookStudio.Application/OpenCode"
ADAPTER = ROOT / "src/BookStudio.OpenCode"
TEST_PROJECT = ROOT / "tests/BookStudio.Tests.OpenCodeSseReconciliation"

REQUIRED = [
    APPLICATION / "IOpenCodeEventReconciler.cs",
    APPLICATION / "OpenCodeEventContracts.cs",
    ADAPTER / "OpenCodeSseParser.cs",
    ADAPTER / "OpenCodeEventNormalizer.cs",
    ADAPTER / "OpenCodeEventDeduplicator.cs",
    ADAPTER / "OpenCodeSessionStatusParser.cs",
    ADAPTER / "OpenCodeEventReconciler.cs",
    TEST_PROJECT / "AGENTS.md",
    TEST_PROJECT / "BookStudio.Tests.OpenCodeSseReconciliation.csproj",
    TEST_PROJECT / "Program.cs",
    TEST_PROJECT / "ContractualOpenCodeSseServer.cs",
    TEST_PROJECT / "OpenCodeSseReconciliationJourney.cs",
]


class OpenCodeSseReconciliationContractTests(unittest.TestCase):
    def test_required_contract_files_exist(self) -> None:
        for path in REQUIRED:
            self.assertTrue(path.exists(), f"Missing OpenCode SSE contract: {path}")

    def test_application_contract_is_provider_neutral_and_complete(self) -> None:
        for path in (
            APPLICATION / "IOpenCodeEventReconciler.cs",
            APPLICATION / "OpenCodeEventContracts.cs",
        ):
            content = path.read_text(encoding="utf-8")
            for forbidden in (
                "HttpClient",
                "HttpRequestMessage",
                "HttpStatusCode",
                "System.Text.Json",
                "JsonElement",
                "StreamReader",
                "Channel",
                "Uri",
                "Authorization",
            ):
                self.assertNotIn(forbidden, content)

        interface = (APPLICATION / "IOpenCodeEventReconciler.cs").read_text(encoding="utf-8")
        self.assertIn("IAsyncEnumerable<OpenCodeReconciledEvent>", interface)
        self.assertIn("WatchAsync", interface)

        contracts = (APPLICATION / "OpenCodeEventContracts.cs").read_text(encoding="utf-8")
        for token in (
            "OpenCodeEventWatchRequest",
            "OpenCodeReconciledEvent",
            "project",
            "global",
            "poll",
            "connected",
            "session_status",
            "provider_event",
            "reconciliation",
            "initial",
            "reconnect",
            "eof",
            "stall",
            "malformed",
            "periodic",
            "sse_reconnect_exhausted",
        ):
            self.assertIn(token, contracts)

    def test_sse_parser_is_incremental_strict_and_bounded(self) -> None:
        parser = (ADAPTER / "OpenCodeSseParser.cs").read_text(encoding="utf-8")
        for token in (
            "UTF8Encoding",
            "throwOnInvalidBytes: true",
            "MaximumLineBytes",
            "MaximumEventDataBytes",
            "MaximumFieldCount",
            '"data"',
            '"event"',
            '"id"',
            '"retry"',
            "ReadAsync",
            "yield return",
        ):
            self.assertIn(token, parser)
        self.assertNotIn("ReadToEndAsync", parser)
        self.assertNotIn("ReadAsStringAsync", parser)

    def test_reconciler_is_compatibility_gated_get_only_and_bounded(self) -> None:
        reconciler = (ADAPTER / "OpenCodeEventReconciler.cs").read_text(encoding="utf-8")
        for token in (
            "IOpenCodeCompatibilityProbe",
            "OpenCodeFeatureIds.EventsProject",
            "OpenCodeFeatureIds.EventsGlobal",
            "OpenCodeFeatureIds.SessionsStatus",
            '"event"',
            '"global/event"',
            '"session/status"',
            "HttpMethod.Get",
            "text/event-stream",
            "ResponseHeadersRead",
            "BoundedChannelOptions",
            "StallTimeout",
            "MaximumConsecutiveFaults",
            "InitialReconnectDelay",
            "MaximumReconnectDelay",
            "Basic",
            "Task.WhenAll",
            "OpenCodeBoundedStatusCache",
            "Queue<string>",
        ):
            self.assertIn(token, reconciler)
        for forbidden in (
            "HttpMethod.Post",
            "HttpMethod.Put",
            "HttpMethod.Patch",
            "HttpMethod.Delete",
            '"prompt_async"',
            '"abort"',
            '"shell"',
            '"command"',
            '"file"',
        ):
            self.assertNotIn(forbidden, reconciler)

    def test_normalizer_deduplicator_and_status_parser_are_safe(self) -> None:
        normalizer = (ADAPTER / "OpenCodeEventNormalizer.cs").read_text(encoding="utf-8")
        for token in (
            '"server.connected"',
            '"session.status"',
            '"directory"',
            '"payload"',
            '"properties"',
            '"sessionID"',
            "OpenCodeSessionStatusParser",
            "MaxDepth",
        ):
            self.assertIn(token, normalizer)

        dedupe = (ADAPTER / "OpenCodeEventDeduplicator.cs").read_text(encoding="utf-8")
        for token in (
            "SHA256",
            "Queue",
            "HashSet",
            "MaximumDedupeEntries",
            "TryAccept",
        ):
            self.assertIn(token, dedupe)

        status = (ADAPTER / "OpenCodeSessionStatusParser.cs").read_text(encoding="utf-8")
        for token in (
            "OpenCodeSessionStatus.Idle",
            "OpenCodeSessionStatus.Busy",
            "OpenCodeSessionStatus.Retry",
            "OpenCodeSessionStatus.Unknown",
            "MaximumStatusEntries",
        ):
            self.assertIn(token, status)

        lifecycle = (ADAPTER / "OpenCodeSessionLifecycleClient.cs").read_text(encoding="utf-8")
        self.assertIn("OpenCodeSessionStatusParser.ParseSnapshot", lifecycle)

    def test_real_journey_covers_stream_reconnect_reconciliation_and_shutdown(self) -> None:
        journey = (TEST_PROJECT / "OpenCodeSseReconciliationJourney.cs").read_text(encoding="utf-8")
        for token in (
            "ParserFramingAsync",
            "ParserBoundsAsync",
            "ProjectStreamAsync",
            "GlobalStreamAsync",
            "DeduplicationAsync",
            "StatusCacheBoundedAsync",
            "EofReconnectAndPollingAsync",
            "MalformedReconnectAsync",
            "StallReconnectAsync",
            "ReconnectExhaustionAsync",
            "AuthenticationAsync",
            "SessionFilterAsync",
            "CancellationAndEarlyDisposalAsync",
            "NO_MUTATION",
            "NO_LEAKED_TASKS",
            "Authorization",
        ):
            self.assertIn(token, journey)

        program = (TEST_PROJECT / "Program.cs").read_text(encoding="utf-8")
        self.assertIn("OPENCODE_SSE_RECONCILIATION_PASS", program)

        server = (TEST_PROJECT / "ContractualOpenCodeSseServer.cs").read_text(encoding="utf-8")
        for token in (
            "TcpListener",
            "text/event-stream",
            "Content-Length",
            "Requests",
            "ActiveConnections",
        ):
            self.assertIn(token, server)

    def test_solution_architecture_and_ci_registration_exist(self) -> None:
        project_path = "tests/BookStudio.Tests.OpenCodeSseReconciliation/BookStudio.Tests.OpenCodeSseReconciliation.csproj"
        solution = (ROOT / "BookStudio.slnx").read_text(encoding="utf-8")
        self.assertIn(project_path, solution)

        policy = json.loads(
            (ROOT / "docs/architecture/architecture-policy.json").read_text(encoding="utf-8")
        )
        projects = {project["name"]: project for project in policy["projects"]}
        self.assertIn("BookStudio.Tests.OpenCodeSseReconciliation", projects)
        self.assertEqual(
            projects["BookStudio.Tests.OpenCodeSseReconciliation"]["allowedBookStudioAssemblyReferences"],
            ["BookStudio.Application", "BookStudio.OpenCode"],
        )

        providers = json.loads(
            (ROOT / "config/ci/providers.json").read_text(encoding="utf-8")
        )
        contracts = {item["id"] for item in providers["contracts"]}
        self.assertIn("dotnet.opencode-sse-reconciliation-integration", contracts)

        workflow = (ROOT / ".github/workflows/02-dotnet-ci.yml").read_text(encoding="utf-8")
        self.assertIn("Run OpenCode SSE reconciliation integration journey", workflow)
        self.assertIn("dotnet-opencode-sse-reconciliation-integration.json", workflow)


if __name__ == "__main__":
    unittest.main()

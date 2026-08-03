from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def test_vs129_orchestrator_contract_is_present():
    source = (ROOT / "src/BookStudio.Autopilot/EditorialJourney/DeterministicEditorialJourneyOrchestrator.cs").read_text(encoding="utf-8")

    assert "DeterministicEditorialJourneyOrchestrator" in source
    assert "EditorialArtifactIdFactory" in source
    assert '"request_fingerprint_conflict"' in source
    assert '"artifact_postcondition_failed"' in source
    assert '"review_requires_revision"' in source
    assert '"preflight_blocked"' in source
    assert "GetReleaseAsync" in source
    assert "RecordFailureBestEffortAsync" in source


def test_vs129_is_idempotent_and_does_not_trust_textual_completion():
    source = (ROOT / "src/BookStudio.Autopilot/EditorialJourney/DeterministicEditorialJourneyOrchestrator.cs").read_text(encoding="utf-8")
    tests = (ROOT / "tests/BookStudio.Tests.EditorialJourney/Program.cs").read_text(encoding="utf-8")

    assert "alreadyPersisted" in source
    assert "VerifyArtifactPostcondition" in source
    assert "VerifyReleasePostcondition" in source
    assert "generation_wrapper_detected" in source
    assert "Resume duplicated draft registration" in tests
    assert "Resume duplicated release preparation" in tests
    assert "Resume duplicated model generation" in tests


def test_vs129_has_isolated_ci_and_documentation():
    workflow = (ROOT / ".github/workflows/04-vs129-editorial-journey.yml").read_text(encoding="utf-8")
    spec = (ROOT / "docs/vertical-slices/VS-129-deterministic-editorial-journey-orchestrator.md").read_text(encoding="utf-8")

    assert "BookStudio.Tests.EditorialJourney" in workflow
    assert "Run deterministic editorial journey tests" in workflow
    assert "request fingerprint" in spec
    assert "does not repeat verified generation" in spec
    assert "sanitized failure event" in spec

from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_vs119_spec_and_dual_tdd_exist() -> None:
    spec = read("docs/specs/VS-119.md")
    red = read("docs/evidence/VS-119/RED_EVIDENCE.md")
    assert "BookLanguageTag" in spec
    assert "UI language and book language are distinct authorities" in spec
    assert "es-ES" in spec and "en-US" in spec
    assert "RED-I" in red and "RED-E" in red


def test_language_contract_is_provider_neutral_and_fail_closed() -> None:
    contracts = read("src/BookStudio.Application/Authoring/LanguageGovernanceContracts.cs")
    orchestrator = read("src/BookStudio.Application/Authoring/LanguageGovernanceOrchestrator.cs")
    for token in (
        "ProjectLanguageAuthority",
        "CompiledLanguageContract",
        "LanguageInvocationContext",
        "LanguageValidationResult",
        "AllowedLanguageScope",
        "PolicyDigest",
        "InstructionDigest",
    ):
        assert token in contracts
    assert "LANGUAGE CONTRACT" in orchestrator
    assert "Required output language" in orchestrator
    assert "RetryRequired" in orchestrator
    assert "LanguageGovernanceConflictException" in orchestrator
    assert "Provider" in contracts and "Model" in contracts


def test_locale_profiles_and_language_drift_are_governed() -> None:
    orchestrator = read("src/BookStudio.Application/Authoring/LanguageGovernanceOrchestrator.cs")
    for locale in ("es-ES", "es-MX", "en-US", "en-GB"):
        assert locale in orchestrator
    assert "LANGUAGE_DRIFT" in orchestrator
    assert "LOCALE_VARIANT" in orchestrator
    assert "CoveredByApprovedScope" in orchestrator


def test_sqlite_authority_is_replay_safe_and_atomic() -> None:
    migration = read("src/BookStudio.Infrastructure/Persistence/Sqlite/Migrations/0056_language_governance.sql")
    store = read("src/BookStudio.Infrastructure/Persistence/Sqlite/Authoring/SqliteLanguageGovernanceStore.cs")
    for table in (
        "language_governance_policies",
        "language_governance_findings",
        "language_governance_decisions",
        "language_governance_receipts",
        "language_governance_history",
        "language_governance_outbox",
    ):
        assert table in migration
        assert table in store
    assert "BeginTransaction" in store
    assert "Stale language governance revision" in store
    assert "Operation reused with a different payload" in store
    assert "ORDER BY revision DESC" in store
    assert "MessageId" in store


def test_vs119_final_evidence_is_complete() -> None:
    for name in ("GREEN_EVIDENCE.md", "M_AUDIT.md", "META_AUDIT.md", "RETROSPEC.md"):
        text = read(f"docs/evidence/VS-119/{name}")
        assert "VS-119" in text
        assert "PASS" in text

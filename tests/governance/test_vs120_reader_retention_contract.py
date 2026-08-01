from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def test_vs120_reader_retention_contract_is_complete():
    spec = (ROOT / "docs/specs/VS-120-reader-retention.md").read_text(encoding="utf-8")
    contracts = (ROOT / "src/BookStudio.Application/Authoring/ReaderRetentionContracts.cs").read_text(encoding="utf-8")
    evaluator = (ROOT / "src/BookStudio.Application/Authoring/ReaderRetentionEvaluator.cs").read_text(encoding="utf-8")

    for required in (
        "reader-engagement authority",
        "reader promise",
        "abandonment map",
        "smallest safe scope",
        "Fail-closed publication gate",
        "exactly-once Outbox",
    ):
        assert required.lower() in spec.lower()

    for required in (
        "ReaderPromise",
        "ReaderRetentionMetric",
        "ReaderRetentionFinding",
        "ReaderCriticAssessment",
        "ReaderRetentionRiskPoint",
        "ReaderRetentionRepairPlan",
        "ReaderRetentionDecision",
        "IReaderRetentionStore",
    ):
        assert required in contracts

    for dimension in (
        "Hook", "Desire", "Conflict", "Novelty", "Progression", "ExpositionLoad",
        "Tension", "Payoff", "EmotionalConnection", "Clarity", "Predictability",
    ):
        assert dimension in contracts
        assert f"ReaderRetentionDimension.{dimension}" in evaluator

    assert "PublicationBlocked" in contracts
    assert "ReaderRetentionRiskBand.Critical" in evaluator
    assert "criticVotes" in evaluator
    assert "SHA256.HashData" in evaluator


def test_vs120_audit_artifacts_exist():
    evidence = ROOT / "docs/evidence/VS-120"
    for name in ("GREEN_EVIDENCE.md", "M_AUDIT.md", "META_AUDIT.md", "RETROSPEC.md"):
        path = evidence / name
        assert path.exists(), name
        text = path.read_text(encoding="utf-8")
        assert "VS-120" in text
        assert "PASS" in text

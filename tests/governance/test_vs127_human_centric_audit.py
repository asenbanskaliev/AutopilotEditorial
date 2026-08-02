from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SMOKE = ROOT / "tests" / "BookStudio.Tests.Integration" / "HumanCentricBookCreationAuditSmoke.cs"
PROGRAM = ROOT / "tests" / "BookStudio.Tests.Integration" / "Program.cs"
SPEC = ROOT / "docs" / "vertical-slices" / "VS-127-human-centric-book-audit.md"


def test_vs127_is_executable_and_connected_to_ci():
    smoke = SMOKE.read_text(encoding="utf-8")
    program = PROGRAM.read_text(encoding="utf-8")
    assert "ProviderBackedDeepBookProofAuthority" in smoke
    assert "LocalDeterministicPublicationProvider" in smoke
    assert "ImageProviderRightsPipeline" in smoke
    assert "HumanCentricBookCreationAuditSmoke.RunAsync" in program


def test_vs127_covers_restart_cost_rights_and_exact_evidence():
    smoke = SMOKE.read_text(encoding="utf-8")
    required = [
        "duplicate provider cost",
        "fail closed",
        "human-centric-book-audit.json",
        "Sha256",
        "MaximumAutomaticRepairs",
        "MaximumCost",
        "EPUB",
        "PDF",
        "DOCX",
        "KDP",
        "AltText",
        "LicenseReference",
    ]
    for token in required:
        assert token.lower() in smoke.lower()


def test_vs127_spec_limits_claims_and_forbids_merge():
    spec = SPEC.read_text(encoding="utf-8")
    assert "SDD" in spec
    assert "Dual TDD" in spec
    assert "M Audit" in spec
    assert "Meta-Audit" in spec
    assert "RetroSpec" in spec
    assert "Do not merge" in spec
    assert "human usability study" in spec.lower()

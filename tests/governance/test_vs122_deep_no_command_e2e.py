from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def test_vs122_deep_no_command_contract_is_complete():
    spec = read("docs/specs/VS-122-deep-no-command-end-to-end-proof.md")
    contracts = read("src/BookStudio.Application/Authoring/DeepBookProofContracts.cs")
    coordinator = read("src/BookStudio.Application/Authoring/DeepBookProofCoordinator.cs")
    store = read("src/BookStudio.Application/Authoring/FileDeepBookProofStore.cs")
    smoke = read("tests/BookStudio.Tests.Integration/DeepBookProofIntegrationSmoke.cs")
    program = read("tests/BookStudio.Tests.Integration/Program.cs")

    for token in [
        "natural-language idea", "survives interruption", "EPUB", "PDF", "DOCX", "KDP",
        "Automatic repair is bounded", "Final readiness", "Atomic file-backed checkpoint store",
    ]:
        assert token in spec

    for token in [
        "IDeepBookProofStore", "DeepBookProofPolicy", "DeepBookArtifact",
        "DeepBookProofCheckpoint", "MaximumRepairAttempts", "EvidenceDigest",
    ]:
        assert token in contracts

    for token in [
        "StartOrResumeAsync", "RecordRepairAsync", "VerifyAndFinalize", "VerifyArtifact",
        "MaximumCost", "MaximumRepairAttempts", "SHA256", "WaitingForDecision",
    ]:
        assert token in coordinator

    for token in ["FileOptions.WriteThrough", "File.Move", "expectedRevision", "Workspace path escapes"]:
        assert token in store

    for token in [
        "Simulate process interruption", "ArtifactVerification", "ReadyForPublication",
        "Terminal replay", "repair budget exhausted",
    ]:
        assert token in smoke

    assert "DeepBookProofIntegrationSmoke.RunAsync" in program


def test_vs122_evidence_and_matrix_exist():
    for path in [
        "docs/evidence/VS-122/RED_EVIDENCE.md",
        "docs/evidence/VS-122/GREEN_EVIDENCE.md",
        "docs/evidence/VS-122/M_AUDIT.md",
        "docs/evidence/VS-122/META_AUDIT.md",
        "docs/evidence/VS-122/RETROSPEC.md",
        "docs/master-plan/product-completion-matrix.md",
    ]:
        assert (ROOT / path).exists(), path

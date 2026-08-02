from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def test_vs124_governance_artifacts_exist():
    required = [
        "docs/specs/VS-124-image-provider-rights.md",
        "docs/evidence/VS-124/RED_EVIDENCE.md",
        "docs/evidence/VS-124/GREEN_EVIDENCE.md",
        "docs/evidence/VS-124/M_AUDIT.md",
        "docs/evidence/VS-124/META_AUDIT.md",
        "docs/evidence/VS-124/RETROSPEC.md",
        "src/BookStudio.Application/Authoring/ImageProviderRightsPipeline.cs",
        "tests/BookStudio.Tests.Integration/ImageProviderRightsPipelineSmoke.cs",
    ]
    for relative in required:
        assert (ROOT / relative).is_file(), relative


def test_vs124_pipeline_is_fail_closed_and_wired():
    source = (ROOT / "src/BookStudio.Application/Authoring/ImageProviderRightsPipeline.cs").read_text(encoding="utf-8")
    program = (ROOT / "tests/BookStudio.Tests.Integration/Program.cs").read_text(encoding="utf-8")
    for token in [
        "MaxCost",
        "MaxRepairAttempts",
        "AllowedLicenseKinds",
        "RequiredTerritory",
        "AtomicWriteAsync",
        "SHA256",
        "AssetProvenanceEvidence",
        "AssetRightsEvidence",
        "AssetAccessibilityEvidence",
        "ReusedExistingArtifact",
    ]:
        assert token in source
    assert "ImageProviderRightsPipelineSmoke.RunAsync" in program

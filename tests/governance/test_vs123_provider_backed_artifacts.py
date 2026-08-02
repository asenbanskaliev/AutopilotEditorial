from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def test_vs123_governance_artifacts_exist():
    required = [
        "docs/specs/VS-123-provider-backed-publication-artifacts.md",
        "docs/evidence/VS-123/RED_EVIDENCE.md",
        "docs/evidence/VS-123/GREEN_EVIDENCE.md",
        "docs/evidence/VS-123/M_AUDIT.md",
        "docs/evidence/VS-123/META_AUDIT.md",
        "docs/evidence/VS-123/RETROSPEC.md",
        "src/BookStudio.Application/Authoring/PublicationArtifactPipeline.cs",
        "tests/BookStudio.Tests.Integration/PublicationArtifactPipelineIntegrationSmoke.cs",
    ]
    for relative in required:
        assert (ROOT / relative).is_file(), relative


def test_vs123_pipeline_is_fail_closed_and_wired():
    source = (ROOT / "src/BookStudio.Application/Authoring/PublicationArtifactPipeline.cs").read_text(encoding="utf-8")
    program = (ROOT / "tests/BookStudio.Tests.Integration/Program.cs").read_text(encoding="utf-8")
    for token in ["MaximumCost", "EnsureInside", "WriteAtomicAsync", "SHA256", "application/epub+zip", "%PDF-1.4", "word/document.xml", "metadata.json"]:
        assert token in source
    assert "PublicationArtifactPipelineIntegrationSmoke.RunAsync" in program

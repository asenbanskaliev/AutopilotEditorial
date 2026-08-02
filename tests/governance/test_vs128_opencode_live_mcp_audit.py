from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def test_live_workflow_uses_repository_secret_ephemerally():
    workflow = (ROOT / ".github/workflows/03-opencode-live-mcp-audit.yml").read_text(encoding="utf-8")
    runner = (ROOT / "scripts/run_opencode_live_mcp_audit.py").read_text(encoding="utf-8")

    assert "secrets.OPENCODE_ZEN_API_KEY" in workflow
    assert "::add-mask::$OPENCODE_ZEN_API_KEY" in workflow
    assert "python -u scripts/run_opencode_live_mcp_audit.py" in workflow
    assert "OPENCODE_AUTH_CONTENT" in runner
    assert '"credentialPersisted": False' in runner
    assert "persisted auth.json" in runner
    assert "OPENCODE_ZEN_API_KEY is missing or implausibly short" in runner


def test_live_audit_is_pinned_bounded_observable_and_free_model_only():
    workflow = (ROOT / ".github/workflows/03-opencode-live-mcp-audit.yml").read_text(encoding="utf-8")
    runner = (ROOT / "scripts/run_opencode_live_mcp_audit.py").read_text(encoding="utf-8")

    assert 'OPENCODE_VERSION = "1.15.5"' in runner
    assert "FREE_MODEL_ALLOWLIST" in runner
    assert "opencode/deepseek-v4-flash-free" in runner
    assert "STAGE {number}/{total} START" in runner
    assert "STAGE {number}/{total} PASS" in runner
    assert "timeout-minutes: 18" in workflow
    assert "secret leakage detected" in runner


def test_strong_journey_uses_real_authoring_and_production_mcps():
    runner = (ROOT / "scripts/run_opencode_live_mcp_audit.py").read_text(encoding="utf-8")

    assert "BookStudio.Mcp.Authoring.csproj" in runner
    assert "BookStudio.Mcp.Production.csproj" in runner
    assert '"autopilot_authoring"' in runner
    assert '"autopilot_production"' in runner
    assert "book.draft.register" in runner
    assert "book.draft.validate" in runner
    assert "book.release.prepare" in runner
    assert "book.preflight.run" in runner
    assert '"stageCount": total' in runner
    assert '"restartRediscovery": True' in runner
    assert '"duplicateRegistrationAttempted": False' in runner
    assert '"duplicateReleasePreparationAttempted": False' in runner

from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def test_live_workflow_uses_repository_secret_without_persisting_it():
    workflow = (ROOT / ".github/workflows/03-opencode-live-mcp-audit.yml").read_text(encoding="utf-8")
    runner = (ROOT / "scripts/run_opencode_live_mcp_audit.py").read_text(encoding="utf-8")

    assert "secrets.OPENCODE_ZEN_API_KEY" in workflow
    assert "::add-mask::$OPENCODE_ZEN_API_KEY" in workflow
    assert "OPENCODE_AUTH_CONTENT" in runner
    assert '"credentialPersisted": False' in runner
    assert "auth.json" not in runner
    assert "OPENCODE_ZEN_API_KEY is missing" in runner


def test_live_audit_is_pinned_bounded_and_free_model_only():
    runner = (ROOT / "scripts/run_opencode_live_mcp_audit.py").read_text(encoding="utf-8")

    assert 'OPENCODE_VERSION = "1.15.5"' in runner
    assert "FREE_MODEL_ALLOWLIST" in runner
    assert "opencode/deepseek-v4-flash-free" in runner
    assert "--model" in runner
    assert "timeout=240" in runner
    assert "no approved free OpenCode Zen model" in runner
    assert "secret leakage detected" in runner


def test_real_mcp_is_published_registered_and_rediscovered():
    runner = (ROOT / "scripts/run_opencode_live_mcp_audit.py").read_text(encoding="utf-8")

    assert "BookStudio.Mcp.csproj" in runner
    assert '"type": "local"' in runner
    assert '"autopilot_editorial"' in runner
    assert '[str(exe), "mcp", "list"]' in runner
    assert "book.artifact.get exactly once" in runner
    assert '"mcpRediscoveredAfterRun": True' in runner

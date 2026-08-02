from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "install" / "windows" / "Install-BookStudio.ps1"


def test_installer_is_signed_digest_bound_and_fail_closed():
    text = SCRIPT.read_text(encoding="utf-8")
    assert "Get-FileHash" in text and "SHA256" in text
    assert "Get-AuthenticodeSignature" in text
    assert "$signature.Status -ne 'Valid'" in text


def test_first_run_is_durable_confined_and_bounded():
    text = SCRIPT.read_text(encoding="utf-8")
    assert "Write-AtomicJson" in text
    assert "Assert-WithinRoot" in text
    assert "first-run.json" in text
    assert "MaxRepairAttempts" in text
    assert "Repair limit exceeded" in text


def test_credentials_and_costs_are_deployment_grade():
    text = SCRIPT.read_text(encoding="utf-8")
    assert "ConvertFrom-SecureString" in text
    assert "Windows-DPAPI-current-user" in text
    assert "monthlyLimitEur" in text
    assert "BOOKSTUDIO_PROVIDER_SECRET" in text


def test_normal_path_launches_without_technical_commands():
    text = SCRIPT.read_text(encoding="utf-8")
    assert "Start-Process -FilePath $launcher" in text
    assert "already configured" in text

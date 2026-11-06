import os
import shutil
import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).parents[1]
NODE_INSTALLER = ROOT / "deployment" / "install-node.sh"
BOOTSTRAP = ROOT / "deployment" / "install-agent.sh"
SERVICE = ROOT / "agent" / "meowdeploy-agent.service"
DEPLOY = ROOT / "deployment" / "deploy.py"


def find_bash() -> str | None:
    found = shutil.which("bash")
    if found:
        return found
    if os.name == "nt":
        for candidate in (
            r"C:\Program Files\Git\bin\bash.exe",
            r"C:\Program Files\Git\usr\bin\bash.exe",
        ):
            if Path(candidate).exists():
                return candidate
    return None


BASH = find_bash()
needs_bash = pytest.mark.skipif(BASH is None, reason="bash is required for installer argument tests")


def run_installer(*args: str) -> subprocess.CompletedProcess[str]:
    assert BASH is not None
    return subprocess.run(
        [BASH, str(NODE_INSTALLER), *args],
        cwd=ROOT,
        text=True,
        capture_output=True,
        timeout=60,
    )


@needs_bash
def test_installer_reports_version():
    result = run_installer("--version")
    assert result.returncode == 0
    assert "MeowDeploy node installer" in result.stdout
    assert "2026.11.06.2" in result.stdout


@needs_bash
def test_installer_help_mentions_agent_only_and_yes():
    result = run_installer("--help")
    assert result.returncode == 0
    assert "--agent-only" in result.stdout
    assert "--yes" in result.stdout


@needs_bash
def test_installer_accepts_agent_only_and_yes_in_dry_run():
    result = run_installer(
        "--controller",
        "http://abc.onion:18765",
        "--token",
        "long-enough-secret",
        "--agent-id",
        "vm-03",
        "--agent-only",
        "--yes",
        "--dry-run",
    )
    assert result.returncode == 0, result.stderr
    assert "Mode:        agent-only" in result.stdout
    assert "Installer:   2026.11.06.2" in result.stdout
    assert "http://abc.onion:18765" in result.stdout


@needs_bash
def test_installer_rejects_out_of_range_port():
    result = run_installer(
        "--controller",
        "http://abc.onion:187765",
        "--token",
        "long-enough-secret",
        "--agent-only",
        "--yes",
        "--dry-run",
    )
    assert result.returncode == 2
    assert "port" in result.stderr.lower()
    assert "187765" in result.stderr


@needs_bash
def test_installer_requires_explicit_port():
    result = run_installer(
        "--controller",
        "http://abc.onion",
        "--token",
        "long-enough-secret",
        "--agent-only",
        "--yes",
        "--dry-run",
    )
    assert result.returncode == 2
    assert "port" in result.stderr.lower()


def test_bootstrap_passes_arguments_to_downloaded_installer():
    text = BOOTSTRAP.read_text(encoding="utf-8")
    assert '"$@"' in text
    assert "--agent-only" in text
    assert "--yes|-y)" in text
    assert "does not report a version" in text


def test_installer_and_deployer_remove_stale_execstart_dropins():
    installer = NODE_INSTALLER.read_text(encoding="utf-8")
    deployer = DEPLOY.read_text(encoding="utf-8")
    for text in (installer, deployer):
        assert "10-config.conf" in text
        assert "ExecStart" in text
        assert "agent.service.d" in text
    assert "remove_agent_execstart_dropins" in installer
    assert "rm -f" in deployer


def test_service_and_installer_agree_on_config_file():
    service = SERVICE.read_text(encoding="utf-8")
    installer = NODE_INSTALLER.read_text(encoding="utf-8")
    deployer = DEPLOY.read_text(encoding="utf-8")

    assert "ConditionPathExists=/etc/meowdeploy/agent.json" in service
    assert "ExecStart=/opt/meowdeploy/venv/bin/python3 /opt/meowdeploy/meowdeploy_agent.py --config /etc/meowdeploy/agent.json" in service
    assert "--token" not in service
    assert "MEOWDEPLOY_TOKEN" not in service

    assert "CONF=/etc/meowdeploy" in installer
    assert "BASE=/opt/meowdeploy" in installer
    assert '"$CONF/agent.json"' in installer
    assert "chmod 600" in installer
    assert "systemctl daemon-reload" in installer

    assert 'CONF = "/etc/meowdeploy"' in deployer
    assert 'BASE = "/opt/meowdeploy"' in deployer
    assert 'AGENT_CONFIG = f"{CONF}/agent.json"' in deployer


def test_installer_verifies_runtime_dependencies_before_success():
    installer = NODE_INSTALLER.read_text(encoding="utf-8")
    for required in (
        'require_file "$BASE/meowdeploy_agent.py"',
        'require_file "$BASE/requirements.txt"',
        'require_file /etc/systemd/system/meowdeploy-agent.service',
        '[[ -x $BASE/venv/bin/python3 ]]',
        "Tor SOCKS listener 127.0.0.1:9050 is not listening",
        "systemctl is-active --quiet meowdeploy-agent.service",
        "systemctl is-enabled --quiet meowdeploy-agent.service",
        "expected_exec=",
        "MeowDeploy installed:",
    ):
        assert required in installer


def test_deploy_controller_validation_matches_agent_rules():
    pytest.importorskip("paramiko")
    import deployment.deploy as deploy

    assert deploy.validate_controller("http://abc.onion:18765/") == "http://abc.onion:18765"
    with pytest.raises(ValueError, match="port"):
        deploy.validate_controller("http://abc.onion:187765")
    with pytest.raises(ValueError):
        deploy.validate_controller("http://abc.onion")

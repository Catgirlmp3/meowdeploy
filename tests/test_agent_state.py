import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parents[1] / "agent"))
from meowdeploy_agent import (  # noqa: E402
    Agent,
    STATE_VERSION,
    _validate_controller,
    _validate_socks_port,
)


def test_null_last_applied_and_old_state_do_not_crash_polling(tmp_path):
    path = tmp_path / "state.json"
    path.write_text(
        json.dumps(
            {
                "version": "old",
                "schedule": {"intervals": "broken"},
                "override": "old-string-override",
                "last_applied": None,
                "processed_commands": None,
                "cpu_sample": [],
            }
        ),
        encoding="utf-8",
    )

    agent = Agent(path, dry_run=True, cores=8)

    assert agent.state["version"] == STATE_VERSION
    assert agent.state["schedule"] is None
    assert agent.state["override"] is None
    assert agent.state["last_applied"] is None
    assert agent.state["processed_commands"] == {}
    assert agent.last_applied_percent() is None
    assert agent.effective_percent() == 0.0
    assert agent.processed_result("missing") is None

    # The old poll loop did state.get("last_applied", {}).get(...), which raises
    # AttributeError when last_applied is explicitly null. The migration keeps
    # the field safe and apply() writes a usable value.
    agent.apply(30)
    assert agent.last_applied_percent() == 30
    reloaded = Agent(path, dry_run=True, cores=8)
    assert reloaded.last_applied_percent() == 30


def test_malformed_last_applied_scalar_is_discarded(tmp_path):
    path = tmp_path / "state.json"
    path.write_text(json.dumps({"last_applied": "30"}), encoding="utf-8")

    agent = Agent(path, dry_run=True, cores=8)

    assert agent.state["last_applied"] is None
    assert agent.last_applied_percent() is None


def test_last_applied_with_missing_or_bad_percent_is_safe(tmp_path):
    path = tmp_path / "state.json"
    path.write_text(
        json.dumps({"last_applied": {"at": "2026-01-01T00:00:00+00:00", "percent": None}}),
        encoding="utf-8",
    )

    agent = Agent(path, dry_run=True, cores=8)

    assert agent.last_applied_percent() is None
    assert agent.status()["effective_percent"] == 0.0


@pytest.mark.parametrize(
    "url",
    [
        "http://abc.onion:18765",
        "https://abc.onion:443/",
        "http://127.0.0.1:18765",
        "http://localhost:18765/",
    ],
)
def test_controller_url_validation_accepts_valid_urls(url):
    _validate_controller(url)


@pytest.mark.parametrize(
    "url",
    [
        "http://abc.onion:187765",
        "http://abc.onion:65536",
        "http://abc.onion:0",
        "http://abc.onion",
        "http://abc.onion:not-a-port",
        "http://abc.onion:18765/api",
        "http://abc.onion:18765/?query=1",
        "ftp://abc.onion:18765",
        "http://user:pass@abc.onion:18765",
    ],
)
def test_controller_url_validation_rejects_malformed_urls(url):
    with pytest.raises(ValueError):
        _validate_controller(url)


def test_bad_controller_port_message_is_clear():
    with pytest.raises(ValueError, match="port"):
        _validate_controller("http://abc.onion:187765")


def test_socks_port_validation():
    _validate_socks_port(9050)
    with pytest.raises(ValueError):
        _validate_socks_port(0)
    with pytest.raises(ValueError):
        _validate_socks_port(70000)

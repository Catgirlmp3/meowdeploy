import datetime as dt
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1] / "agent"))
from meowdeploy_agent import (  # noqa: E402
    Agent,
    quota,
    scheduled_percent,
    validate_schedule,
)

S = {
    "timezone": "Europe/Dublin",
    "intervals": [
        {"start": "00:00", "end": "06:00", "percent": 75},
        {"start": "06:00", "end": "17:00", "percent": 35},
        {"start": "17:00", "end": "18:00", "percent": 50},
        {"start": "18:00", "end": "00:00", "percent": 60},
    ],
}


def test_quota_uses_actual_core_count():
    assert quota(75, 20) == 1500
    assert quota(35, 20) == 700
    assert quota(30, 8) == 240


def test_schedule_and_midnight():
    validate_schedule(S)
    assert scheduled_percent(S, dt.datetime(2026, 1, 1, 2)) == 75
    assert scheduled_percent(S, dt.datetime(2026, 1, 1, 21)) == 60


def test_override_persists(tmp_path):
    a = Agent(tmp_path / "state.json", True)
    a.command("set_schedule", S)
    scheduled = a.effective_percent()
    a.command("temporary_override", {"percent": 25, "duration_seconds": 3600})
    b = Agent(tmp_path / "state.json", True)
    assert b.effective_percent() == 25
    b.command("clear_override", {})
    assert b.effective_percent() == scheduled


def test_reject_gap():
    bad = {
        "intervals": [
            {"start": "00:00", "end": "05:00", "percent": 1},
            {"start": "06:00", "end": "00:00", "percent": 1},
        ]
    }
    try:
        validate_schedule(bad)
        assert False
    except ValueError:
        pass


def test_stop_miner_does_not_restart_it(tmp_path, monkeypatch):
    a = Agent(tmp_path / "state.json", False)
    calls = []
    monkeypatch.setattr(a, "_systemctl", lambda *args: calls.append(("systemctl", *args)))
    monkeypatch.setattr(a, "apply", lambda *args, **kwargs: calls.append(("apply",)))

    result = a.command("stop_miner", {})

    assert result["action"] == "stop"
    assert calls == [("systemctl", "stop", "meowdeploy-miner.service")]


def test_apply_now_runs_apply(tmp_path, monkeypatch):
    a = Agent(tmp_path / "state.json", True)
    a.command("set_schedule", S)
    monkeypatch.setattr(a, "apply", lambda percent=None: 41.0)

    assert a.command("apply_now", {}) == {"ok": True, "effective_percent": 41.0}


def test_processed_command_result_survives_restart(tmp_path):
    path = tmp_path / "state.json"
    a = Agent(path, True)
    a.remember_result("command-1", {"ok": True, "effective_percent": 35})

    b = Agent(path, True)
    assert b.processed_result("command-1") == {"ok": True, "effective_percent": 35}


def test_agent_cores_are_used_for_quota(tmp_path, monkeypatch):
    a = Agent(tmp_path / "state.json", True, cores=8)
    calls = []
    monkeypatch.setattr("meowdeploy_agent.subprocess.run", lambda *args, **kwargs: calls.append(args))
    a.apply(30)
    assert calls == []
    assert a.state["last_applied"]["percent"] == 30

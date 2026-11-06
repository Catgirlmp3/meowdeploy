#!/usr/bin/env python3
"""MeowDeploy local agent.

The agent is outbound-only: it polls a controller over Tor, validates every
command against a small allowlist, and never executes shell text received from
the controller. Use --dry-run to inspect behavior without touching systemd.
"""
from __future__ import annotations

import argparse
import datetime as dt
import glob
import hashlib
import hmac
import json
import logging
import os
import re
import subprocess
import sys
import time
from pathlib import Path
from typing import Any
from urllib.parse import urlparse
from zoneinfo import ZoneInfo

import requests

VERSION = "0.2.0"
DEFAULT_STATE = "/var/lib/meowdeploy/state.json"
DEFAULT_CONFIG = "/etc/meowdeploy/agent.json"
DEFAULT_MINER_SERVICE = "meowdeploy-miner.service"
MAX_PROCESSED_COMMANDS = 200
CONFIG_EXIT_CODE = 78
log = logging.getLogger("meowdeploy-agent")


def detect_cores() -> int:
    """Return CPUs available to this process, respecting affinity if set."""
    try:
        return max(1, len(os.sched_getaffinity(0)))
    except (AttributeError, OSError):
        return max(1, os.cpu_count() or 1)


def quota(percent: float, cores: int | None = None) -> int:
    if not 0 <= percent <= 100:
        raise ValueError("percent must be between 0 and 100")
    cores = detect_cores() if cores is None else int(cores)
    if cores < 1:
        raise ValueError("cores must be positive")
    return round(percent * cores)


def parse_minutes(value: str) -> int:
    if not re.fullmatch(r"(?:[01]\d|2[0-3]):[0-5]\d", value):
        raise ValueError("invalid time")
    h, m = map(int, value.split(":"))
    return h * 60 + m


def validate_schedule(schedule: dict[str, Any]) -> None:
    if not isinstance(schedule, dict):
        raise ValueError("schedule must be an object")
    intervals = schedule.get("intervals")
    if not isinstance(intervals, list) or not intervals:
        raise ValueError("schedule must contain intervals")
    timezone = schedule.get("timezone", "Europe/Dublin")
    if not isinstance(timezone, str):
        raise ValueError("timezone must be a string")
    try:
        ZoneInfo(timezone)
    except Exception as error:
        raise ValueError(f"invalid timezone: {error}") from error

    spans = []
    for item in intervals:
        if not isinstance(item, dict):
            raise ValueError("each interval must be an object")
        try:
            start, end = parse_minutes(item["start"]), parse_minutes(item["end"])
            percent = float(item["percent"])
        except (KeyError, TypeError, ValueError) as error:
            raise ValueError("invalid interval") from error
        if start == end:
            raise ValueError("zero-length interval")
        if not 0 <= percent <= 100:
            raise ValueError("invalid CPU percentage")
        spans.append((start, end if end > start else 1440))
    spans.sort()
    if spans[0][0] != 0 or spans[-1][1] != 1440:
        raise ValueError("schedule must cover the full day")
    for previous, current in zip(spans, spans[1:]):
        if current[0] != previous[1]:
            raise ValueError("schedule has a gap or overlap")


def scheduled_percent(schedule: dict[str, Any], when: dt.datetime) -> float:
    validate_schedule(schedule)
    minute = when.hour * 60 + when.minute
    for item in schedule["intervals"]:
        start, end = parse_minutes(item["start"]), parse_minutes(item["end"])
        end = 1440 if end <= start else end
        if start <= minute < end:
            return float(item["percent"])
    return float(schedule["intervals"][-1]["percent"])


def _validate_agent_id(value: str) -> None:
    if not re.fullmatch(r"[A-Za-z0-9_.:-]{1,128}", value):
        raise ValueError("agent_id must be 1-128 characters from A-Z, a-z, 0-9, _, ., :, -")


def _validate_controller(value: str) -> None:
    parsed = urlparse(value)
    if parsed.scheme not in {"http", "https"} or not parsed.netloc:
        raise ValueError("controller must be an http(s) URL")
    if parsed.hostname and not parsed.hostname.endswith(".onion") and parsed.hostname not in {"127.0.0.1", "localhost"}:
        log.warning("controller is not an onion address; this is only sensible for local testing")


class Agent:
    def __init__(
        self,
        state_path: str = DEFAULT_STATE,
        dry_run: bool = False,
        miner_service: str = DEFAULT_MINER_SERVICE,
        cores: int | None = None,
    ):
        self.path = Path(state_path)
        self.dry_run = dry_run
        self.miner_service = miner_service
        self.cores = int(cores or detect_cores())
        self.started_monotonic = time.monotonic()
        self.state: dict[str, Any] = {
            "version": 1,
            "schedule": None,
            "override": None,
            "last_applied": None,
            "processed_commands": {},
        }
        self._load()

    def _load(self) -> None:
        if not self.path.exists():
            return
        try:
            loaded = json.loads(self.path.read_text(encoding="utf-8-sig"))
            if not isinstance(loaded, dict):
                raise ValueError("state file is not an object")
            self.state.update(loaded)
        except (OSError, ValueError) as error:
            log.warning("could not read state %s: %s; starting with defaults", self.path, error)
            try:
                self.path.replace(self.path.with_suffix(self.path.suffix + ".corrupt"))
            except OSError:
                pass
        if not isinstance(self.state.get("processed_commands"), dict):
            self.state["processed_commands"] = {}

    def save(self) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.path.with_suffix(self.path.suffix + ".tmp")
        tmp.write_text(json.dumps(self.state, indent=2), encoding="utf-8")
        try:
            os.chmod(tmp, 0o600)
        except OSError:
            pass
        os.replace(tmp, self.path)

    def effective_percent(self, now: dt.datetime | None = None) -> float:
        now = now or dt.datetime.now(dt.timezone.utc)
        if now.tzinfo is None:
            now = now.replace(tzinfo=dt.timezone.utc)
        override = self.state.get("override")
        if override:
            try:
                expires = dt.datetime.fromisoformat(override["expires"])
                if expires.tzinfo is None:
                    expires = expires.replace(tzinfo=dt.timezone.utc)
                if expires > now:
                    return float(override["percent"])
            except (KeyError, TypeError, ValueError):
                log.warning("discarding malformed override")
            self.state["override"] = None
            self.save()
        schedule = self.state.get("schedule")
        if not schedule:
            return 0.0
        local = now.astimezone(ZoneInfo(schedule.get("timezone", "Europe/Dublin")))
        return scheduled_percent(schedule, local)

    def _systemctl(self, action: str, unit: str | None = None) -> None:
        command = ["systemctl", action]
        if unit:
            command.append(unit)
        try:
            subprocess.run(command, check=True, capture_output=True, text=True, timeout=30)
        except subprocess.CalledProcessError as error:
            detail = (error.stderr or error.stdout or "").strip()
            raise RuntimeError(f"systemctl {action} failed: {detail or error.returncode}") from error
        except (OSError, subprocess.TimeoutExpired) as error:
            raise RuntimeError(f"systemctl {action} failed: {error}") from error

    def apply(self, percent: float | None = None) -> float:
        percent = float(self.effective_percent() if percent is None else percent)
        content = f"[Service]\nCPUQuota={quota(percent, self.cores)}%\nCPUAccounting=true\n"
        changed = True
        if not self.dry_run:
            target = Path("/etc/systemd/system") / f"{self.miner_service}.d" / "90-meowdeploy.conf"
            try:
                changed = not target.exists() or target.read_text(encoding="utf-8") != content
            except OSError:
                changed = True
            if changed:
                target.parent.mkdir(parents=True, exist_ok=True)
                tmp = target.with_suffix(target.suffix + ".tmp")
                tmp.write_text(content, encoding="utf-8")
                try:
                    os.chmod(tmp, 0o644)
                except OSError:
                    pass
                os.replace(tmp, target)
                self._systemctl("daemon-reload")
                self._systemctl("restart", self.miner_service)
        self.state["last_applied"] = {
            "percent": percent,
            "at": dt.datetime.now(dt.timezone.utc).isoformat(),
            "changed": changed,
        }
        self.save()
        return percent

    def public_state(self) -> dict[str, Any]:
        hidden = {"processed_commands", "cpu_sample"}
        return {key: value for key, value in self.state.items() if key not in hidden}

    def status(self) -> dict[str, Any]:
        ready = time.monotonic() - self.started_monotonic >= 120
        return {
            "version": VERSION,
            "cores": self.cores,
            "effective_percent": self.effective_percent(),
            "state": self.public_state(),
            "metrics_ready": ready,
            "metrics": self.metrics() if ready else None,
        }

    def command(self, name: str, payload: dict[str, Any]) -> dict[str, Any]:
        if not isinstance(payload, dict):
            raise ValueError("payload must be an object")
        if name == "get_status":
            return self.status()
        if name == "set_schedule":
            payload = dict(payload)
            payload.setdefault("timezone", "Europe/Dublin")
            validate_schedule(payload)
            self.state["schedule"] = payload
        elif name == "temporary_override":
            try:
                percent = float(payload["percent"])
                duration = int(payload["duration_seconds"])
            except (KeyError, TypeError, ValueError) as error:
                raise ValueError("invalid override") from error
            if not 0 <= percent <= 100 or duration <= 0 or duration > 7 * 86400:
                raise ValueError("invalid override")
            self.state["override"] = {
                "percent": percent,
                "expires": (dt.datetime.now(dt.timezone.utc) + dt.timedelta(seconds=duration)).isoformat(),
            }
        elif name == "clear_override":
            self.state["override"] = None
        elif name == "apply_now":
            pass  # The common apply below is the operation.
        elif name in {"start_miner", "stop_miner", "restart_miner"}:
            action = name.removesuffix("_miner")
            if not self.dry_run:
                self._systemctl(action, self.miner_service)
            self.save()
            return {"ok": True, "action": action, "effective_percent": self.effective_percent()}
        else:
            raise ValueError("unsupported command")

        self.save()
        applied = self.apply()
        return {"ok": True, "effective_percent": applied}

    def metrics(self) -> dict[str, Any]:
        result: dict[str, Any] = {
            "uptime_seconds": None,
            "cpu_utilization_percent": None,
            "temperature_c": None,
            "hashrate": None,
            "load_1m": None,
            "miner_active": None,
            "errors": [],
        }

        def warning(message: str) -> None:
            log.debug("metrics: %s", message)
            result["errors"].append(message)

        try:
            result["uptime_seconds"] = float(Path("/proc/uptime").read_text(encoding="utf-8").split()[0])
        except (OSError, ValueError, IndexError) as error:
            warning(f"uptime unavailable: {error}")
        try:
            result["load_1m"] = float(Path("/proc/loadavg").read_text(encoding="utf-8").split()[0])
        except (OSError, ValueError, IndexError) as error:
            warning(f"load average unavailable: {error}")
        try:
            stat = Path("/proc/stat").read_text(encoding="utf-8").splitlines()[0].split()[1:]
            total = sum(map(int, stat))
            idle = int(stat[3]) + (int(stat[4]) if len(stat) > 4 else 0)
            previous = self.state.get("cpu_sample")
            self.state["cpu_sample"] = {"total": total, "idle": idle}
            if previous and total > previous["total"]:
                result["cpu_utilization_percent"] = round(
                    100 * (1 - (idle - previous["idle"]) / (total - previous["total"])), 1
                )
            self.save()
        except (OSError, ValueError, IndexError, TypeError, KeyError) as error:
            warning(f"CPU sample unavailable: {error}")

        for path in sorted(glob.glob("/sys/class/thermal/thermal_zone*/temp")):
            try:
                result["temperature_c"] = round(float(Path(path).read_text(encoding="utf-8").strip()) / 1000, 1)
                break
            except (OSError, ValueError) as error:
                warning(f"temperature read failed: {path}: {error}")

        try:
            process = subprocess.run(
                ["systemctl", "is-active", self.miner_service],
                check=False,
                capture_output=True,
                text=True,
                timeout=3,
            )
            result["miner_active"] = process.stdout.strip() or "unknown"
        except (OSError, subprocess.TimeoutExpired) as error:
            warning(f"systemctl is-active failed: {error}")

        # XMRig's HTTP API is bound to localhost and restricted in miner.json.
        try:
            response = requests.get(
                "http://127.0.0.1:18088/2/summary",
                timeout=1,
                proxies={"http": None, "https": None},
            )
            response.raise_for_status()
            data = response.json()
            hashrate = data.get("hashrate", {}).get("total", [])
            if hashrate and hashrate[0] is not None:
                result["hashrate"] = round(float(hashrate[0]), 2)
        except (requests.RequestException, ValueError, TypeError, KeyError, IndexError) as error:
            log.debug("miner API unavailable: %s", error)
        return result

    def processed_result(self, command_id: str) -> dict[str, Any] | None:
        if not command_id:
            return None
        return self.state.get("processed_commands", {}).get(command_id)

    def remember_result(self, command_id: str, result: dict[str, Any]) -> None:
        if not command_id:
            return
        processed = self.state.setdefault("processed_commands", {})
        processed[command_id] = result
        if len(processed) > MAX_PROCESSED_COMMANDS:
            oldest = sorted(processed, key=lambda key: processed[key].get("at", ""))
            for key in oldest[: len(processed) - MAX_PROCESSED_COMMANDS]:
                del processed[key]
        self.save()


def signed_request(url: str, token: str, body: dict[str, Any], socks_port: int = 9050) -> dict[str, Any]:
    raw = json.dumps(body, separators=(",", ":")).encode()
    signature = hmac.new(token.encode(), raw, hashlib.sha256).hexdigest()
    proxies = {
        "http": f"socks5h://127.0.0.1:{socks_port}",
        "https": f"socks5h://127.0.0.1:{socks_port}",
    }
    response = requests.post(
        url,
        data=raw,
        headers={"Content-Type": "application/json", "X-MeowDeploy-Signature": signature},
        proxies=proxies,
        timeout=20,
    )
    response.raise_for_status()
    result = response.json()
    if not isinstance(result, dict):
        raise ValueError("controller returned a non-object response")
    return result


def poll(agent: Agent, controller: str, agent_id: str, token: str, interval: int, socks_port: int) -> None:
    """Outbound-only polling loop. A Tor onion URL may be supplied as controller."""
    controller = controller.rstrip("/")
    while True:
        # Execute schedules/overrides locally even while the controller is offline.
        try:
            target = agent.effective_percent()
            previous = agent.state.get("last_applied", {}).get("percent")
            if agent.state.get("schedule") is not None and target != previous:
                agent.apply(target)
        except Exception:
            log.exception("local schedule/override apply failed")

        try:
            result = signed_request(
                controller + "/api/poll",
                token,
                {"agent_id": agent_id, "status": agent.command("get_status", {})},
                socks_port,
            )
            commands = result.get("commands", [])
            if not isinstance(commands, list):
                raise ValueError("controller returned invalid commands")
            log.debug("poll ok (%d command(s) queued)", len(commands))
            for command in commands:
                if not isinstance(command, dict):
                    log.warning("ignoring malformed command")
                    continue
                command_id = str(command.get("id") or "")
                cached = agent.processed_result(command_id)
                if cached is not None:
                    ack = {**cached, "duplicate": True}
                else:
                    try:
                        ack = agent.command(str(command.get("name") or ""), command.get("payload") or {})
                    except Exception as error:
                        log.warning("command %s failed: %s", command.get("name"), error)
                        ack = {"ok": False, "error": f"{type(error).__name__}: {error}"}
                    ack["at"] = dt.datetime.now(dt.timezone.utc).isoformat()
                    agent.remember_result(command_id, ack)
                try:
                    signed_request(
                        controller + "/api/ack",
                        token,
                        {"agent_id": agent_id, "command_id": command_id, "result": ack},
                        socks_port,
                    )
                except Exception as error:
                    log.warning("ack for %s failed: %s; controller will redeliver", command_id, error)
        except Exception as error:
            # Most common causes: Tor SocksPort not listening, onion address/token
            # mismatch (HTTP 401), or no PySocks support in the running interpreter.
            log.warning("poll to %s failed: %s: %s", controller, type(error).__name__, error)
        time.sleep(max(5, interval))


def load_config(path: Path) -> dict[str, Any]:
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(data, dict):
        raise ValueError("config must be a JSON object")
    return data


def _pick(cli_value: Any, env_name: str, config: dict[str, Any], key: str, default: Any = None) -> Any:
    if cli_value is not None:
        return cli_value
    env_value = os.environ.get(env_name)
    if env_value is not None and env_value != "":
        return env_value
    if key in config:
        return config[key]
    return default


def main() -> int:
    parser = argparse.ArgumentParser(description="MeowDeploy fleet agent")
    parser.add_argument("--config", help=f"JSON config path (default: {DEFAULT_CONFIG} when present)")
    parser.add_argument("--state", default=None, help=f"state path (default: {DEFAULT_STATE})")
    parser.add_argument("--dry-run", action="store_true", help="do not change systemd")
    parser.add_argument("--status", action="store_true", help="print status once and exit")
    parser.add_argument("--version", action="version", version=VERSION)
    parser.add_argument("--controller", default=None)
    parser.add_argument("--agent-id", default=None)
    parser.add_argument("--token", default=None)
    parser.add_argument("--poll-seconds", type=int, default=None)
    parser.add_argument("--socks-port", type=int, default=None)
    parser.add_argument("--miner-service", default=None)
    parser.add_argument("--cores", type=int, default=None)
    parser.add_argument("--verbose", action="store_true", help="log every poll attempt, not just failures")
    args = parser.parse_args()

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)s %(message)s",
    )

    config: dict[str, Any] = {}
    config_path = Path(args.config) if args.config else Path(DEFAULT_CONFIG)
    if args.config or config_path.exists():
        try:
            config = load_config(config_path)
        except (OSError, ValueError) as error:
            parser.error(f"cannot read config {config_path}: {error}")

    controller = _pick(args.controller, "MEOWDEPLOY_CONTROLLER", config, "controller")
    agent_id = _pick(args.agent_id, "MEOWDEPLOY_AGENT_ID", config, "agent_id")
    token = _pick(args.token, "MEOWDEPLOY_TOKEN", config, "token")
    state_path = _pick(args.state, "MEOWDEPLOY_STATE", config, "state_path", DEFAULT_STATE)
    poll_seconds = int(_pick(args.poll_seconds, "MEOWDEPLOY_POLL_SECONDS", config, "poll_seconds", 30))
    socks_port = int(_pick(args.socks_port, "MEOWDEPLOY_SOCKS_PORT", config, "socks_port", 9050))
    miner_service = _pick(
        args.miner_service, "MEOWDEPLOY_MINER_SERVICE", config, "miner_service", DEFAULT_MINER_SERVICE
    )
    cores = int(_pick(args.cores, "MEOWDEPLOY_CORES", config, "cores", 0) or 0) or detect_cores()

    agent = Agent(str(state_path), args.dry_run, str(miner_service), cores)
    if args.status:
        print(json.dumps(agent.status(), indent=2))
        return 0

    if not (controller and agent_id and token):
        # Exit non-zero so systemd does not present an "active" service that
        # silently prints status once every RestartSec and never polls.
        log.error("controller, agent_id, and token are required for polling; use --status for a one-shot report")
        return CONFIG_EXIT_CODE

    try:
        _validate_controller(str(controller))
        _validate_agent_id(str(agent_id))
        if len(str(token)) < 8:
            raise ValueError("token must be at least 8 characters")
    except ValueError as error:
        log.error("invalid configuration: %s", error)
        return CONFIG_EXIT_CODE

    log.info(
        "starting poll loop: controller=%s agent_id=%s socks_port=%s poll_seconds=%s miner_service=%s cores=%s",
        controller,
        agent_id,
        socks_port,
        max(5, poll_seconds),
        miner_service,
        cores,
    )
    poll(agent, str(controller), str(agent_id), str(token), max(5, poll_seconds), socks_port)
    return 0


if __name__ == "__main__":
    sys.exit(main())

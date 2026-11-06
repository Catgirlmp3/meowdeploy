#!/usr/bin/env python3
"""MeowDeploy local agent. Safe simulation mode is enabled with --dry-run."""
from __future__ import annotations
import argparse, datetime as dt, glob, hashlib, hmac, json, logging, os, re, subprocess, time
import requests
from pathlib import Path
from zoneinfo import ZoneInfo

VERSION = "0.1.0"
DEFAULT_STATE = "/var/lib/meowdeploy/state.json"
MINER_SERVICE = "meowdeploy-miner.service"
log = logging.getLogger("meowdeploy-agent")

def quota(percent: float, cores: int = 20) -> int:
    if not 0 <= percent <= 100: raise ValueError("percent must be between 0 and 100")
    return round(percent * cores)

def parse_minutes(value: str) -> int:
    if not re.fullmatch(r"(?:[01]\d|2[0-3]):[0-5]\d", value): raise ValueError("invalid time")
    h, m = map(int, value.split(":")); return h * 60 + m

def validate_schedule(schedule: dict) -> None:
    intervals = schedule.get("intervals", [])
    if not intervals: raise ValueError("schedule must contain intervals")
    spans = []
    for item in intervals:
        start, end = parse_minutes(item["start"]), parse_minutes(item["end"])
        if start == end: raise ValueError("zero-length interval")
        if not 0 <= float(item["percent"]) <= 100: raise ValueError("invalid CPU percentage")
        spans.append((start, end if end > start else 1440))
    spans.sort()
    if spans[0][0] != 0 or spans[-1][1] != 1440: raise ValueError("schedule must cover the full day")
    for previous, current in zip(spans, spans[1:]):
        if current[0] != previous[1]: raise ValueError("schedule has a gap or overlap")

def scheduled_percent(schedule: dict, when: dt.datetime) -> float:
    validate_schedule(schedule)
    minute = when.hour * 60 + when.minute
    for item in schedule["intervals"]:
        start, end = parse_minutes(item["start"]), parse_minutes(item["end"])
        end = 1440 if end <= start else end
        if start <= minute < end: return float(item["percent"])
    return float(schedule["intervals"][-1]["percent"])

class Agent:
    def __init__(self, state_path=DEFAULT_STATE, dry_run=False):
        self.path, self.dry_run = Path(state_path), dry_run
        self.started_monotonic = time.monotonic()
        self.state = {"version": 1, "schedule": None, "override": None, "last_applied": None}
        if self.path.exists(): self.state.update(json.loads(self.path.read_text()))
    def save(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.path.with_suffix(".tmp"); tmp.write_text(json.dumps(self.state, indent=2)); os.replace(tmp, self.path)
    def effective_percent(self, now=None):
        now = now or dt.datetime.now(dt.timezone.utc)
        override = self.state.get("override")
        if override and dt.datetime.fromisoformat(override["expires"]) > now: return float(override["percent"])
        if override: self.state["override"] = None; self.save()
        schedule = self.state.get("schedule")
        if not schedule: return 0.0
        local = now.astimezone(ZoneInfo(schedule.get("timezone", "Europe/Dublin")))
        return scheduled_percent(schedule, local)
    def apply(self, percent=None):
        percent = self.effective_percent() if percent is None else float(percent)
        content = f"[Service]\nCPUQuota={quota(percent)}%\nCPUAccounting=true\n"
        if not self.dry_run:
            p = Path("/etc/systemd/system") / f"{MINER_SERVICE}.d" / "90-meowdeploy.conf"; p.parent.mkdir(parents=True, exist_ok=True); p.write_text(content)
            subprocess.run(["systemctl", "daemon-reload"], check=True)
            subprocess.run(["systemctl", "restart", MINER_SERVICE], check=True)
        self.state["last_applied"] = {"percent": percent, "at": dt.datetime.now(dt.timezone.utc).isoformat()}; self.save()
        return percent
    def command(self, name, payload):
        if name == "set_schedule": validate_schedule(payload); self.state["schedule"] = payload
        elif name == "temporary_override":
            percent = float(payload["percent"]); duration = int(payload["duration_seconds"])
            if not 0 <= percent <= 100 or duration <= 0 or duration > 7*86400: raise ValueError("invalid override")
            self.state["override"] = {"percent": percent, "expires": (dt.datetime.now(dt.timezone.utc)+dt.timedelta(seconds=duration)).isoformat()}
        elif name == "clear_override": self.state["override"] = None
        elif name == "apply_now": pass
        elif name in {"start_miner", "stop_miner", "restart_miner"}:
            action = name.removesuffix("_miner")
            if not self.dry_run: subprocess.run(["systemctl", action, MINER_SERVICE], check=True)
        elif name == "get_status":
            ready = time.monotonic() - self.started_monotonic >= 120
            return {"version": VERSION, "effective_percent": self.effective_percent(), "state": self.state, "metrics_ready": ready, "metrics": self.metrics() if ready else None}
        else: raise ValueError("unsupported command")
        self.save(); self.apply(); return {"ok": True, "effective_percent": self.effective_percent()}

    def metrics(self):
        result = {"uptime_seconds": None, "cpu_utilization_percent": None, "temperature_c": None, "hashrate": None}
        try: result["uptime_seconds"] = float(Path("/proc/uptime").read_text().split()[0])
        except (OSError, ValueError, IndexError): pass
        try: result["load_1m"] = float(Path("/proc/loadavg").read_text().split()[0])
        except (OSError, ValueError, IndexError): pass
        try:
            stat = Path("/proc/stat").read_text().splitlines()[0].split()[1:]
            total = sum(map(int, stat)); idle = int(stat[3]) + (int(stat[4]) if len(stat) > 4 else 0)
            previous = self.state.get("cpu_sample")
            self.state["cpu_sample"] = {"total": total, "idle": idle}
            if previous and total > previous["total"]:
                result["cpu_utilization_percent"] = round(100 * (1 - (idle - previous["idle"]) / (total - previous["total"])), 1)
            self.save()
        except (OSError, ValueError, IndexError, TypeError, KeyError): pass
        for path in glob.glob("/sys/class/thermal/thermal_zone*/temp"):
            try: result["temperature_c"] = round(float(Path(path).read_text().strip()) / 1000, 1); break
            except (OSError, ValueError): pass
        # XMRig HTTP API must be enabled in the miner config with restricted localhost access.
        try:
            response = requests.get("http://127.0.0.1:18088/2/summary", timeout=1)
            response.raise_for_status(); data = response.json()
            hr = data.get("hashrate", {}).get("total", [])
            if hr: result["hashrate"] = round(float(hr[0]), 2)
        except (requests.RequestException, ValueError, TypeError, KeyError): pass
        return result

def signed_request(url, token, body, socks_port=9050):
    raw = json.dumps(body, separators=(",", ":")).encode(); signature = hmac.new(token.encode(), raw, hashlib.sha256).hexdigest()
    proxies = {"http": f"socks5h://127.0.0.1:{socks_port}", "https": f"socks5h://127.0.0.1:{socks_port}"}
    response = requests.post(url, data=raw, headers={"Content-Type":"application/json", "X-MeowDeploy-Signature":signature}, proxies=proxies, timeout=20)
    response.raise_for_status(); return response.json()

def poll(agent, controller, agent_id, token, interval, socks_port):
    """Outbound-only polling loop. A Tor onion URL may be supplied as controller."""
    while True:
        # Execute schedules/override expiry locally even while the controller is offline.
        try:
            target = agent.effective_percent()
            previous = agent.state.get("last_applied", {}).get("percent")
            if agent.state.get("schedule") is not None and target != previous:
                agent.apply(target)
        except Exception:
            log.exception("local schedule/override apply failed")
        try:
            result = signed_request(controller.rstrip('/') + '/api/poll', token, {"agent_id":agent_id, "status":agent.command("get_status", {})}, socks_port)
            commands = result.get("commands", [])
            log.debug("poll ok (%d command(s) queued)", len(commands))
            for command in commands:
                try:
                    signed_request(controller.rstrip('/') + '/api/ack', token, {"agent_id":agent_id, "command_id":command["id"], "result":agent.command(command["name"], command.get("payload", {}))}, socks_port)
                except Exception as error:
                    log.warning("command %s failed: %s", command.get("name"), error)
                    signed_request(controller.rstrip('/') + '/api/ack', token, {"agent_id":agent_id, "command_id":command.get("id"), "error":str(error)}, socks_port)
        except Exception as error:
            # Most common causes: Tor SocksPort not listening, onion address/token
            # mismatch (HTTP 401 from the controller), or no PySocks support in
            # whichever python interpreter actually runs this file.
            log.warning("poll to %s failed: %s: %s", controller, type(error).__name__, error)
        time.sleep(interval)

def main():
    parser = argparse.ArgumentParser(); parser.add_argument("--state", default=DEFAULT_STATE); parser.add_argument("--dry-run", action="store_true"); parser.add_argument("--controller"); parser.add_argument("--agent-id"); parser.add_argument("--token"); parser.add_argument("--poll-seconds", type=int, default=30); parser.add_argument("--socks-port", type=int, default=9050); parser.add_argument("--verbose", action="store_true", help="log every poll attempt, not just failures")
    args = parser.parse_args()
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    agent = Agent(args.state, args.dry_run)
    if args.controller and args.agent_id and args.token:
        log.info("starting poll loop: controller=%s agent_id=%s socks_port=%s poll_seconds=%s", args.controller, args.agent_id, args.socks_port, max(5, args.poll_seconds))
        poll(agent, args.controller, args.agent_id, args.token, max(5, args.poll_seconds), args.socks_port)
    else:
        # This is the quiet failure mode that makes the service look "active" while
        # doing nothing: no --controller/--agent-id/--token means we never enter the
        # poll loop at all, we just print status once and exit (then systemd restarts
        # us in 15s and we do it again, forever, with no network activity).
        log.warning("no --controller/--agent-id/--token given — printing status once and exiting instead of polling")
        print(json.dumps(agent.command("get_status", {}), indent=2))
if __name__ == "__main__": main()

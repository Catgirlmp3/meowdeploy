#!/usr/bin/env python3
"""Deploy the reviewed MeowDeploy agent over SSH.

This helper intentionally has no shell-command passthrough. It uploads the
agent, requirements, service unit, and a 0600 JSON config, then runs a fixed
install/verify sequence. It does not install the miner; use install-node.sh on
the host for a full node installation.
"""
from __future__ import annotations

import argparse
import concurrent.futures
import getpass
import io
import json
import os
import pathlib
import re
import shlex
import sys
from urllib.parse import urlparse

import paramiko

DEPLOYER_VERSION = "2026.11.06.2"
ROOT = pathlib.Path(__file__).resolve().parents[1]
AGENT_FILE = ROOT / "agent" / "meowdeploy_agent.py"
REQUIREMENTS_FILE = ROOT / "agent" / "requirements.txt"
SERVICE_FILE = ROOT / "agent" / "meowdeploy-agent.service"
BASE = "/opt/meowdeploy"
CONF = "/etc/meowdeploy"
STATE = "/var/lib/meowdeploy"
AGENT_CONFIG = f"{CONF}/agent.json"
AGENT_SERVICE = "/etc/systemd/system/meowdeploy-agent.service"


def validate_controller(value: str) -> str:
    """Return a normalised controller URL or raise a clear ValueError."""
    parsed = urlparse(value)
    if parsed.scheme not in {"http", "https"} or not parsed.netloc or not parsed.hostname:
        raise ValueError("controller must be an http(s) URL with a host, for example http://abc.onion:18765")
    if parsed.username is not None or parsed.password is not None:
        raise ValueError("controller URL must not contain credentials")
    try:
        port = parsed.port
    except ValueError as error:
        raise ValueError(f"controller has an invalid port: {error}") from error
    if port is None:
        raise ValueError("controller URL must include an explicit port, for example http://abc.onion:18765")
    if not 1 <= port <= 65535:
        raise ValueError(f"controller port must be between 1 and 65535, got {port}")
    if parsed.path not in {"", "/"} or parsed.params or parsed.query or parsed.fragment:
        raise ValueError("controller URL must not include a path, query, or fragment")
    if not parsed.hostname.lower().endswith(".onion") and parsed.hostname not in {"127.0.0.1", "localhost"}:
        print(f"Warning: controller host '{parsed.hostname}' is not a .onion address; use it only for local testing.", file=sys.stderr)
    return value.rstrip("/")


def run(client: paramiko.SSHClient, command: str, timeout: int = 60) -> tuple[int, str, str]:
    _, stdout, stderr = client.exec_command(command, timeout=timeout)
    out = stdout.read().decode("utf-8", "replace").strip()
    err = stderr.read().decode("utf-8", "replace").strip()
    return stdout.channel.recv_exit_status(), out, err


def parse_servers(path: pathlib.Path) -> list[tuple[str, str]]:
    servers: list[tuple[str, str]] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        parts = line.split()
        if len(parts) > 2:
            raise ValueError(f"invalid server line: {line!r}")
        host = parts[0]
        agent_id = parts[1] if len(parts) == 2 else host
        servers.append((host, agent_id))
    return servers


def deploy(
    server: tuple[str, str],
    password: str,
    args: argparse.Namespace,
) -> dict[str, object]:
    host, agent_id = server
    result: dict[str, object] = {"host": host, "agent_id": agent_id, "ok": False, "error": None}
    client = paramiko.SSHClient()
    client.load_system_host_keys()
    try:
        client.connect(
            host,
            username=args.user,
            password=password,
            look_for_keys=False,
            allow_agent=False,
            timeout=15,
            banner_timeout=15,
            auth_timeout=15,
        )
        sftp = client.open_sftp()
        try:
            status, _, err = run(
                client,
                f"install -d -m 750 {shlex.quote(BASE)} {shlex.quote(CONF)} {shlex.quote(STATE)} "
                "&& install -d -m 755 /etc/systemd/system/meowdeploy-miner.service.d",
            )
            if status != 0:
                raise RuntimeError(f"could not create directories: {err}")

            sftp.put(str(AGENT_FILE), f"{BASE}/meowdeploy_agent.py.new")
            sftp.put(str(REQUIREMENTS_FILE), f"{BASE}/requirements.txt.new")
            sftp.put(str(SERVICE_FILE), "/etc/systemd/system/meowdeploy-agent.service.new")

            config = {
                "controller": args.controller.rstrip("/"),
                "agent_id": agent_id,
                "token": args.token,
                "state_path": f"{STATE}/state.json",
                "miner_service": args.miner_service,
                "socks_port": args.socks_port,
                "poll_seconds": args.poll_seconds,
            }
            sftp.putfo(
                io.BytesIO((json.dumps(config, indent=2) + "\n").encode()),
                f"{CONF}/agent.json.new",
            )

            install_command = " && ".join(
                [
                    f"install -m 755 {BASE}/meowdeploy_agent.py.new {BASE}/meowdeploy_agent.py",
                    f"install -m 644 {BASE}/requirements.txt.new {BASE}/requirements.txt",
                    f"install -m 644 /etc/systemd/system/meowdeploy-agent.service.new {AGENT_SERVICE}",
                    f"install -m 600 {CONF}/agent.json.new {AGENT_CONFIG}",
                    f"rm -f {BASE}/meowdeploy_agent.py.new {BASE}/requirements.txt.new "
                    "/etc/systemd/system/meowdeploy-agent.service.new",
                    f"test -x {BASE}/venv/bin/python3 || python3 -m venv {BASE}/venv",
                    f"{BASE}/venv/bin/pip install -q -r {BASE}/requirements.txt",
                    # Older installers left an ExecStart override pointing at
                    # command-line controller/token arguments. Remove any such
                    # drop-in before the daemon reload.
                    "if [ -d /etc/systemd/system/meowdeploy-agent.service.d ]; then "
                    "grep -rlE '^[[:space:]]*ExecStart[[:space:]]*=' "
                    "/etc/systemd/system/meowdeploy-agent.service.d --include='*.conf' "
                    "| xargs -r rm -f || true; "
                    "rm -f /etc/systemd/system/meowdeploy-agent.service.d/10-config.conf; "
                    "rmdir /etc/systemd/system/meowdeploy-agent.service.d 2>/dev/null || true; fi",
                ]
            )
            status, _, err = run(client, install_command, timeout=300)
            if status != 0:
                raise RuntimeError(f"agent install failed: {err}")

            verify_command = " && ".join(
                [
                    f"test -f {AGENT_CONFIG}",
                    f"test -x {BASE}/venv/bin/python3",
                    f"test -f {BASE}/meowdeploy_agent.py",
                    f"test -f {BASE}/requirements.txt",
                    f"test -f {AGENT_SERVICE}",
                    f"[ \"$(stat -c '%a' {AGENT_CONFIG})\" = 600 ]",
                    "systemd-analyze verify /etc/systemd/system/meowdeploy-agent.service",
                    "systemctl daemon-reload",
                    "for i in $(seq 1 60); do "
                    "ss -ltn 2>/dev/null | grep -Eq '127\\.0\\.0\\.1:9050([[:space:]]|$)' && break; "
                    "sleep 1; done",
                    "ss -ltn 2>/dev/null | grep -Eq '127\\.0\\.0\\.1:9050([[:space:]]|$)'",
                    "systemctl enable --now meowdeploy-agent.service",
                    "systemctl is-active --quiet meowdeploy-agent.service",
                    "systemctl is-enabled --quiet meowdeploy-agent.service",
                    "systemctl show meowdeploy-agent.service -p ExecStart --value "
                    f"| grep -F -- '{BASE}/venv/bin/python3 {BASE}/meowdeploy_agent.py "
                    f"--config {AGENT_CONFIG}' >/dev/null",
                ]
            )
            status, _, err = run(client, verify_command, timeout=120)
            if status != 0:
                _, diagnostics, _ = run(
                    client,
                    "systemctl status meowdeploy-agent.service --no-pager -l; "
                    "journalctl -u meowdeploy-agent.service -n 30 --no-pager",
                    timeout=30,
                )
                raise RuntimeError(f"agent verification failed: {err}\n{diagnostics}".strip())

            result["ok"] = True
        finally:
            sftp.close()
    except Exception as error:  # noqa: BLE001 - each host reports its own failure
        result["error"] = f"{type(error).__name__}: {error}"
    finally:
        client.close()
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description="Deploy the MeowDeploy agent over SSH")
    parser.add_argument("--servers", default="deployment/servers.txt", help="one host [agent-id] per line")
    parser.add_argument("--workers", type=int, default=5)
    parser.add_argument("--user", default="root")
    parser.add_argument("--controller", required=True, help="controller onion URL")
    parser.add_argument("--token", default=None, help="shared HMAC token (or MEOWDEPLOY_TOKEN)")
    parser.add_argument("--socks-port", type=int, default=9050)
    parser.add_argument("--poll-seconds", type=int, default=30)
    parser.add_argument("--miner-service", default="meowdeploy-miner.service")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--version", action="version", version=f"MeowDeploy SSH deployer {DEPLOYER_VERSION}")
    args = parser.parse_args()

    try:
        args.controller = validate_controller(args.controller)
    except ValueError as error:
        parser.error(str(error))
    args.token = args.token or os.environ.get("MEOWDEPLOY_TOKEN")
    if not args.token:
        args.token = getpass.getpass("Agent token: ")
    if not 8 <= len(args.token) <= 256:
        parser.error("--token must be 8-256 characters")
    if not 5 <= args.poll_seconds <= 3600:
        parser.error("--poll-seconds must be between 5 and 3600")
    if not 1 <= args.socks_port <= 65535:
        parser.error("--socks-port must be a valid TCP port")

    servers = parse_servers(pathlib.Path(args.servers))
    if not servers:
        parser.error("server list is empty")
    for _, agent_id in servers:
        if not re.fullmatch(r"[A-Za-z0-9_.:-]{1,128}", agent_id):
            parser.error(f"invalid agent id: {agent_id!r}")

    print(f"MeowDeploy SSH deployer {DEPLOYER_VERSION}")
    if args.dry_run:
        for host, agent_id in servers:
            print(f"DRY RUN {host} agent={agent_id} controller={args.controller}")
        return 0

    password = getpass.getpass("Root SSH password (not stored): ")
    all_ok = True
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, min(5, args.workers))) as pool:
        for result in pool.map(lambda server: deploy(server, password, args), servers):
            prefix = "OK " if result["ok"] else "FAIL "
            suffix = f" {result['error']}" if result["error"] else ""
            print(f"{prefix}{result['host']} agent={result['agent_id']}{suffix}")
            all_ok = all_ok and bool(result["ok"])
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())

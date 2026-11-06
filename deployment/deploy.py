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

ROOT = pathlib.Path(__file__).resolve().parents[1]
AGENT_FILE = ROOT / "agent" / "meowdeploy_agent.py"
REQUIREMENTS_FILE = ROOT / "agent" / "requirements.txt"
SERVICE_FILE = ROOT / "agent" / "meowdeploy-agent.service"
BASE = "/opt/meowdeploy"
CONF = "/etc/meowdeploy"
STATE = "/var/lib/meowdeploy"


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
                    "install -m 644 /etc/systemd/system/meowdeploy-agent.service.new "
                    "/etc/systemd/system/meowdeploy-agent.service",
                    f"install -m 600 {CONF}/agent.json.new {CONF}/agent.json",
                    f"rm -f {BASE}/meowdeploy_agent.py.new {BASE}/requirements.txt.new "
                    "/etc/systemd/system/meowdeploy-agent.service.new",
                    f"test -x {BASE}/venv/bin/python3 || python3 -m venv {BASE}/venv",
                    f"{BASE}/venv/bin/pip install -q -r {BASE}/requirements.txt",
                    "rm -f /etc/systemd/system/meowdeploy-agent.service.d/10-config.conf; "
                    "{ rmdir /etc/systemd/system/meowdeploy-agent.service.d 2>/dev/null || true; }",
                ]
            )
            status, _, err = run(client, install_command, timeout=300)
            if status != 0:
                raise RuntimeError(f"agent install failed: {err}")

            status, _, err = run(
                client,
                "systemd-analyze verify /etc/systemd/system/meowdeploy-agent.service && "
                "systemctl daemon-reload && "
                "systemctl enable --now meowdeploy-agent.service && "
                "systemctl is-active --quiet meowdeploy-agent.service",
                timeout=60,
            )
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
    args = parser.parse_args()

    parsed = urlparse(args.controller)
    if parsed.scheme not in {"http", "https"} or not parsed.netloc:
        parser.error("--controller must be an http(s) URL")
    args.token = args.token or os.environ.get("MEOWDEPLOY_TOKEN")
    if not args.token:
        args.token = getpass.getpass("Agent token: ")
    if len(args.token) < 8:
        parser.error("--token must be at least 8 characters")
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

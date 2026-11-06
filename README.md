# MeowDeploy

MeowDeploy is a Windows fleet controller plus a Debian/Ubuntu node installer. The
controller authenticates agents with a shared HMAC token and exposes a localhost-only
API through a Tor onion service. Agents poll outbound through Tor, apply a small
allowlist of commands, and keep their schedule locally when the controller is offline.

Use it only on hosts you own or are explicitly authorized to administer. Mining can
violate a hosting provider's terms, and a controller token is effectively a root-level
fleet credential. Review the installer before running it.

## What is implemented

- One-command full node install: Tor, Python venv, agent, XMRig, systemd units,
  huge-page helper, optional MSR setup, and verification.
- Agent-only install for protocol testing or hosts that already run a miner.
- Config-driven agent (`/etc/meowdeploy/agent.json`, mode `0600`); tokens are not
  embedded in the systemd unit or process command line.
- Per-host CPU math based on the host's actual `nproc`, not a hard-coded 20 cores.
- Schedule and override evaluation that continues while the controller is offline.
- Controller auto-registration: an agent id is added to the dashboard on its first
  authenticated poll, even when it differs from the IP address in the server list.
- Command IDs with agent-side deduplication, controller-side pending queues, and
  best-effort persistence across controller restarts.
- Stop/start/restart, schedule, override, status, health metrics, temperature,
  hashrate, miner service state, and diagnostics in the WinForms UI.
- A safe SSH deployer for the agent only. It runs a fixed install sequence and does
  not accept arbitrary shell commands.

## Quick start

### 1. Start the controller

Set a real token before launching anything that is reachable through Tor:

```powershell
$env:MEOWDEPLOY_TOKEN = 'replace-with-a-long-random-secret'
dotnet run --project controller\MeowDeploy.Controller.csproj
```

The controller listens on `127.0.0.1:18765`. Expose that listener with a Tor onion
service; do not bind it to the LAN. See [docs/TOR_SETUP.md](docs/TOR_SETUP.md).

The fallback token `meowmeowmeow` exists only so a local UI smoke test can start
without configuration. Do not use it on a real fleet.

### 2. Install a node

On each Debian/Ubuntu x86_64 host, as root:

```bash
curl -fsSL https://raw.githubusercontent.com/Catgirlmp3/meowdeploy/feature/fleet-controller-mvp/deployment/install-agent.sh | bash -s -- \
  --controller http://YOUR_CONTROLLER_ONION.onion:18765 \
  --agent-id "$(hostname -s)" \
  --token 'replace-with-the-same-long-random-secret' \
  --cpu-percent 30
```

The installer prints the host, agent id, controller, CPU quota, pool, and wallet before
changing anything and requires you to type `install`. For a reviewed, unattended
deployment, add `--yes` after verifying those values. To print the resolved plan
without touching the host, add `--dry-run`.

Agent-only mode installs Tor and the agent but no miner, huge pages, or MSR tuning:

```bash
curl -fsSL https://raw.githubusercontent.com/Catgirlmp3/meowdeploy/feature/fleet-controller-mvp/deployment/install-agent.sh | bash -s -- \
  --controller http://YOUR_CONTROLLER_ONION.onion:18765 \
  --agent-id "$(hostname -s)" \
  --token 'replace-with-the-same-long-random-secret' \
  --agent-only --yes
```

The installer writes:

| Path | Purpose |
| --- | --- |
| `/opt/meowdeploy/meowdeploy_agent.py` | Agent |
| `/opt/meowdeploy/venv` | Python dependencies (`requests[socks]`, PySocks) |
| `/etc/meowdeploy/agent.json` | Controller URL, agent id, token, state path (mode `0600`) |
| `/etc/meowdeploy/miner.json` | XMRig config (full install only, mode `0600`) |
| `/var/lib/meowdeploy/state.json` | Local schedule, override, last apply, command dedupe |
| `/etc/systemd/system/meowdeploy-agent.service` | Agent unit |
| `/etc/systemd/system/meowdeploy-miner.service` | Miner unit (full install only) |

### 3. Verify

```bash
systemctl status meowdeploy-agent.service meowdeploy-miner.service tor --no-pager
systemctl show meowdeploy-miner.service -p CPUQuotaPerSecUSec -p ActiveState
ss -ltnp | grep 9050
journalctl -u meowdeploy-agent.service -n 50 --no-pager
```

The agent does not print its status to the shell in normal service operation. Use
`journalctl` for errors and the controller's diagnostics panel for current health.

## Agent-only deployment over SSH

`deployment/deploy.py` is for an already-provisioned host. It uploads the agent,
requirements, service unit, and a generated `agent.json`, creates the venv, removes
the old token-in-`ExecStart` drop-in, and verifies the service. It does **not** install
the miner.

```text
deployment/servers.txt
  172.16.3.3 vm-03
  172.16.3.4 vm-04
```

```powershell
python -m pip install -r deployment\requirements.txt
$env:MEOWDEPLOY_TOKEN = 'replace-with-the-shared-token'
python deployment\deploy.py `
  --servers deployment\servers.txt `
  --controller http://YOUR_CONTROLLER_ONION.onion:18765
```

The deployer uses `~/.ssh/known_hosts` and does not auto-add unknown host keys. The
root password is held in memory for the session only. It caps concurrency at five.

## Configuration

The agent resolves values in this order: command line, environment, JSON config,
built-in default. The systemd unit passes only `--config /etc/meowdeploy/agent.json`.

Example config:

```json
{
  "controller": "http://YOUR_CONTROLLER_ONION.onion:18765",
  "agent_id": "vm-03",
  "token": "replace-with-the-shared-token",
  "state_path": "/var/lib/meowdeploy/state.json",
  "miner_service": "meowdeploy-miner.service",
  "socks_port": 9050,
  "poll_seconds": 30
}
```

Keep the file mode `0600` and owned by root. To rotate the token, update this file on
each agent and restart `meowdeploy-agent.service`; then update `MEOWDEPLOY_TOKEN` and
restart the controller.

CPU percentages are relative to the host's available CPUs. On an 8-core host, 30%
becomes `CPUQuota=240%`; on a 20-core host it becomes `600%`. The agent uses the CPU
affinity mask when available, so a systemd `CPUAffinity` restriction is respected.

## Controller operation

- **Servers** are persisted in `%APPDATA%\MeowDeploy\servers.json`.
- **Discovered agents** are merged automatically after their first authenticated poll.
- **Pending commands** are persisted best-effort in
  `%APPDATA%\MeowDeploy\pending-commands.json`.
- **Commands** are redelivered until the agent acknowledges them. The agent remembers
  the last 200 command ids so a lost acknowledgement does not re-run a command.
- **Schedules** are local JSON state. The agent keeps applying the schedule and
  override expiry while the controller is offline.
- **Status** includes effective CPU, core count, state, miner service state, load,
  temperature when available, hashrate from the restricted localhost XMRig API, and
  any metric-collection errors.

## Security model

- The controller API binds to `127.0.0.1` only; Tor exposes it as an onion service.
- Every request is HMAC-SHA256 signed with the shared token.
- The agent never executes controller-supplied shell text. It supports only:
  `set_schedule`, `temporary_override`, `clear_override`, `apply_now`, `get_status`,
  `start_miner`, `stop_miner`, and `restart_miner`.
- The installer verifies the XMRig release digest from GitHub before installing it.
- MSR and huge-page failures are treated as optimization failures, not reasons to
  weaken kernel or host security.
- Agent tokens live in a root-only config file. The old `--token`-on-the-command-line
  drop-in is removed by both install paths.

Limitations still worth knowing:

- The shared token is fleet-wide; per-agent credentials and Windows credential storage
  are not implemented.
- The controller's pending queue is best-effort and in-memory plus a JSON file; it is
  not a transactional database.
- The HMAC request format does not yet include a nonce/timestamp, so it does not have
  full replay protection against an attacker who can observe and immediately replay a
  request inside Tor.
- Full-install, rollback, and uninstall flows should still be exercised on a disposable
  VM before production use.
- `deploy.py` intentionally does not install the miner; use `install-node.sh` for that.

## Tests

```powershell
python -m pytest -q
dotnet build controller\MeowDeploy.Controller.csproj -c Release
python -m py_compile agent\meowdeploy_agent.py deployment\deploy.py
```

The Python tests cover schedule validation, overrides, per-core quota math, command
deduplication, and the stop-miner regression where a stop used to be followed by an
unconditional restart.

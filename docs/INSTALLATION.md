# Installation

Build the controller:

```powershell
dotnet build controller\MeowDeploy.Controller.csproj -c Release
```

Run the tests:

```powershell
python -m pytest -q
python -m py_compile agent\meowdeploy_agent.py deployment\deploy.py
```

## Full node

Run as root on Debian/Ubuntu x86_64. The installer asks for confirmation unless you
pass `--yes`.

Because the `.onion` hostname is long, typing the full `--controller` URL directly in a
terminal can cause line-wrapping that mangles the port (for example `18765` becomes
`187765`). Use environment variables so the shell expands them cleanly:

```bash
export MEOWDEPLOY_CONTROLLER="http://YOUR_CONTROLLER_ONION.onion:18765"
export MEOWDEPLOY_TOKEN='replace-with-a-long-random-token'
export MEOWDEPLOY_AGENT_ID="$(hostname -s)"
echo "$MEOWDEPLOY_CONTROLLER"   # verify the URL is intact before running

curl -fsSL https://raw.githubusercontent.com/Catgirlmp3/meowdeploy/feature/fleet-controller-mvp/deployment/install-agent.sh | bash -s -- \
  --controller "$MEOWDEPLOY_CONTROLLER" \
  --token "$MEOWDEPLOY_TOKEN" \
  --cpu-percent 30
```

The installer validates the port up front and exits with a clear error if it is out of
range (for example `187765`).

The script installs Tor, a Python venv, the agent, and XMRig. It verifies the XMRig
release digest, writes root-only config files, validates the systemd units, starts the
services, and prints a failure diagnostic instead of claiming success when a unit is
not active. Add `--dry-run` to print the resolved host plan without changing anything.

## Agent only

Use `--agent-only` when the host already has a miner or when you are testing the
control plane:

```bash
curl -fsSL https://raw.githubusercontent.com/Catgirlmp3/meowdeploy/feature/fleet-controller-mvp/deployment/install-agent.sh | bash -s -- \
  --controller http://YOUR_CONTROLLER_ONION.onion:18765 \
  --token 'replace-with-a-long-random-token' \
  --agent-only --yes
```

For SSH-based agent-only deployment, copy `deployment/servers.txt.example` to
`deployment/servers.txt`, add `host [agent-id]` lines, and run:

```powershell
python -m pip install -r deployment\requirements.txt
$env:MEOWDEPLOY_TOKEN = 'replace-with-a-long-random-token'
python deployment\deploy.py --servers deployment\servers.txt `
  --controller http://YOUR_CONTROLLER_ONION.onion:18765
```

`deploy.py` uses the known-host database, prompts once for the root password, uploads
the reviewed files, creates the venv, writes `/etc/meowdeploy/agent.json` with mode
`0600`, and verifies the service. It does not install the miner.

## Paths

- Agent: `/opt/meowdeploy/meowdeploy_agent.py`
- Python venv: `/opt/meowdeploy/venv`
- Agent config: `/etc/meowdeploy/agent.json` (mode `0600`)
- Miner config: `/etc/meowdeploy/miner.json` (mode `0600`, full install only)
- State: `/var/lib/meowdeploy/state.json`
- Units: `/etc/systemd/system/meowdeploy-agent.service` and
  `meowdeploy-miner.service`

## Verification

```bash
systemctl is-active meowdeploy-agent.service meowdeploy-miner.service tor
systemctl show meowdeploy-miner.service -p CPUQuotaPerSecUSec -p ActiveState
journalctl -u meowdeploy-agent.service -n 50 --no-pager
```

The agent's CPU quota is calculated from the host's actual CPU count. A 30% cap on an
8-core host is `CPUQuota=240%`; on a 20-core host it is `CPUQuota=600%`.

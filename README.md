# MeowDeploy

MeowDeploy is intended to be a Windows fleet controller and a standalone Linux agent/miner installer. The controller and agent prototype exist, but the all-in-one Linux installer is **not yet implemented**. The current installer deliberately refuses a full install; `--agent-only` is for protocol testing and does not install mining, huge pages, or MSR tuning.

This guide first describes the complete intended installation as a dry-run scenario, then gives the safe commands that are actually available today.

## Intended full-install walkthrough (simulation, not executed on a host)

This is how the finished root-shell installer should behave on one Debian/Proxmox host. Expected outputs below are illustrative. No commands in this section were run against your VM.

### 1. Start from the Windows controller

I would start the dedicated Tor instance and check the controller onion file:

```powershell
Get-Content C:\Tools\meowdeploy-tor\hidden_service\hostname
```

Expected: your controller onion hostname. I would start the app with the same shared test token configured on the Linux agent:

```powershell
$env:MEOWDEPLOY_TOKEN = 'meowmeowmeow'
dotnet run --project controller\MeowDeploy.Controller.csproj
```

Expected: the WinForms dashboard opens and the local API listens on `127.0.0.1:18765`.

### 2. Paste one installer command into the VM root shell

The intended full installer command is:

```bash
curl -fsSL https://raw.githubusercontent.com/Catgirlmp3/meowdeploy/feature/fleet-controller-mvp/deployment/install-agent.sh | bash -s -- \
  --controller http://2zcfiyzbxs7x7ige2qj7iorncwzw2ua6p77qc6gi77o3dpy4olszosqd.onion:18765 \
  --agent-id "$(hostname -s)" \
  --token meowmeowmeow \
  --cpu-percent 30
```

The finished installer should show a short menu before making changes:

```text
MeowDeploy host setup
[1] Install agent and miner (recommended)
[2] Install agent only
[3] Repair/update existing installation
[4] Uninstall MeowDeploy components
Choose [1-4]:
```

I would choose `1`. It should confirm the host, controller onion, wallet destination, pool, and CPU cap before proceeding. Mining should not begin until the operator confirms the displayed values.

### 3. Intended automatic setup and checks

The installer should then:

1. Check root privileges, Debian architecture, systemd, disk space, and whether paths already contain unrelated files.
2. Install Tor, Python, certificates, curl, and required packages. It should append a loopback SOCKS listener to existing Tor configuration only if absent, preserve all other lines, restart Tor, and confirm the SOCKS port is listening.
3. Download the MeowDeploy agent and standalone miner files into MeowDeploy-owned paths (planned: `/opt/meowdeploy`, `/etc/meowdeploy`, `/var/lib/meowdeploy`). It must not clone or invoke MeowKit.
4. Download XMRig release metadata and the Linux asset. It must check the published SHA-256 digest, retry a bounded number of times, remove corrupt temporary artifacts, and stop with a useful error if verification still fails.
5. Write the miner JSON using the MeowKit pool and wallet values, but use MeowDeploy paths and service names. Validate it as JSON and check required values are present before atomically installing it with root-only permissions.
6. Configure the full detected CPU worker range, then apply 30% of the 20-core processor as `CPUQuota=600%`. CPU quota, not a P-core-only thread restriction, controls the ceiling.
7. Configure 2 MiB huge pages within a conservative memory budget. Try 1 GiB pages only when the kernel exposes them; inability to allocate them should be reported and should not abort installation.
8. Attempt MSR setup only where supported. A denied or unsupported MSR operation should be logged and treated as an optimization failure, not as a reason to weaken host security.
9. Create `meowdeploy-agent.service` and a separate `meowdeploy-miner.service`. Installation and normal operation should not stream logs into the shell. The app should show host state, hashrate, and CPU temperature; error details belong behind an in-app diagnostics view.
10. Validate generated systemd units, reload systemd, start services, check their active state, check the agent's Tor connection, and report each result.

Expected successful summary:

```text
Agent service: active
Miner service: active
Tor SOCKS: listening on 127.0.0.1:9050
CPU quota: 600% (30% of 20 cores)
Miner config: valid
Huge pages: configured or explicitly reported unavailable
MSR: enabled or explicitly reported unavailable
Controller: reachable through Tor (first check-in may take up to one poll interval)
```

### 4. Intended controller check

I would wait for the host to show Online, then confirm the displayed CPU allocation is 30%. I would use the app to issue a short test override (for example 20% for 5 minutes), verify the agent acknowledges it, and confirm it returns to the schedule when it expires. Disconnecting the Windows controller must not stop the local schedule.

### 5. What the current code would actually do

The full command above currently exits immediately with status 3 and explains that full standalone miner installation is not implemented. This is intentional: the current agent still targets `meowkit.service`, and no MeowDeploy-owned miner service or validated XMRig installer exists yet. Do not use the simulated success output as evidence of a real installation.

## Available safe checks today

Build the Windows controller:

```powershell
dotnet build controller\MeowDeploy.Controller.csproj -c Release
```

Compile-check the Python agent:

```powershell
python -m py_compile agent\meowdeploy_agent.py
```

Run agent-only installer mode only when intentionally testing the controller agent path:

```bash
curl -fsSL https://raw.githubusercontent.com/Catgirlmp3/meowdeploy/feature/fleet-controller-mvp/deployment/install-agent.sh | bash -s -- \
  --controller http://2zcfiyzbxs7x7ige2qj7iorncwzw2ua6p77qc6gi77o3dpy4olszosqd.onion:18765 \
  --agent-id "$(hostname -s)" --token meowmeowmeow --agent-only
```

Agent-only mode does not configure a miner. For local Python tests, install `agent/requirements.txt` and `pytest`, then run `python -m pytest -q` from the repository root.

## Useful host commands (once installed)

```bash
# Overall service state
systemctl status meowdeploy-agent.service meowdeploy-miner.service tor

# Check Tor SOCKS listener
ss -ltnp | grep 9050

# Show effective miner quota
systemctl show meowdeploy-miner.service -p CPUQuotaPerSecUSec -p ActiveState

# Restart agent / miner
systemctl restart meowdeploy-agent.service
systemctl restart meowdeploy-miner.service

# Stop / start miner quickly
systemctl stop meowdeploy-miner.service
systemctl start meowdeploy-miner.service
```

Those service names are the planned standalone names. They are not installed by the current agent-only prototype. The commands above are quiet status/control commands; no log tail is part of the normal workflow.

## Current limitations

- The agent/controller command protocol needs durable acknowledgements and retry tracking before fleet use.
- The agent currently manages `meowkit.service`; that integration must be replaced by the standalone MeowDeploy miner service.
- Schedule expiry needs a persistent local evaluation loop and rollback handling.
- Per-agent secrets and protected Windows credential storage are not implemented.
- Live hashrate and CPU utilization are not implemented.
- Full-install, rollback, and uninstall flows have not been executed on a disposable Linux VM.

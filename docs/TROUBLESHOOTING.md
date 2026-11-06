# Troubleshooting

Use these checks when the Ubuntu VM does not appear Online in the Windows controller.
Run agent checks on the VM as root. Do not print the full token in a ticket or chat.

## 1. Confirm which installer actually ran

The bootstrap prints its version and the version of the downloaded installer:

```bash
curl -fsSL https://raw.githubusercontent.com/Catgirlmp3/meowdeploy/feature/fleet-controller-mvp/deployment/install-agent.sh | bash -s -- --version
```

A current installer also reports its version at the start, in `--dry-run`, and in the
final success line. If `--agent-only` or `--yes` is rejected, the installer is older
than the publishing branch. Re-run the one-line command and check that the bootstrap
reports `MeowDeploy bootstrap 2026.11.06.2` (or newer). The bootstrap refuses to run a
downloaded installer that does not report a version.

### Long onion URLs and terminal line-wrapping

A `.onion` hostname is long (56 characters). When you type or paste the full
`--controller http://<onion>:18765` argument directly into a terminal, the line can
wrap mid-argument and the shell may mangle it — for example `18765` can become
`187765`. The agent then rejects the port as invalid (`InvalidURL: Failed to parse`),
and the check-in never happens.

**Recommended fix:** Set the controller URL (and token) as environment variables so the
shell expands them cleanly, with no risk of line-wrapping corruption:

```bash
export MEOWDEPLOY_CONTROLLER="http://<onion>.onion:18765"
export MEOWDEPLOY_TOKEN="your-shared-token"
export MEOWDEPLOY_AGENT_ID="ubuntu"

echo "$MEOWDEPLOY_CONTROLLER"   # verify the URL is intact before running

curl -fsSL 'https://raw.githubusercontent.com/Catgirlmp3/meowdeploy/feature/fleet-controller-mvp/deployment/install-agent.sh' \
  | bash -s -- \
  --controller "$MEOWDEPLOY_CONTROLLER" \
  --agent-id "$MEOWDEPLOY_AGENT_ID" \
  --token "$MEOWDEPLOY_TOKEN" \
  --agent-only --yes
```

Always verify with `echo "$MEOWDEPLOY_CONTROLLER"` before running, and confirm the port
is `18765` (not `187765`). The installer validates the port up front and exits with a
clear error if it is out of range.

## 2. Service state and configuration

```bash
systemctl status meowdeploy-agent.service --no-pager -l
systemctl is-active meowdeploy-agent.service
systemctl is-enabled meowdeploy-agent.service
journalctl -u meowdeploy-agent.service -n 80 --no-pager
```

If systemd logs `ConditionPathExists=/etc/meowdeploy/agent.json was not met`, the config
file is missing. Check it without revealing the token:

```bash
sudo test -f /etc/meowdeploy/agent.json && echo "config present"
sudo stat -c '%a %U:%G %n' /etc/meowdeploy/agent.json
sudo jq '{controller,agent_id,socks_port,poll_seconds,state_path,miner_service,token_length:(.token|length)}' \
  /etc/meowdeploy/agent.json
```

`controller` must use port `18765`, not `187765`. The file must be mode `0600`, owned by
root. A current install re-run creates or repairs it:

```bash
curl -fsSL https://raw.githubusercontent.com/Catgirlmp3/meowdeploy/feature/fleet-controller-mvp/deployment/install-agent.sh | bash -s -- \
  --controller http://YOUR_CONTROLLER_ONION.onion:18765 \
  --agent-id "$(hostname -s)" \
  --token 'replace-with-the-same-long-random-secret' \
  --agent-only --yes
```

## 3. Stale ExecStart drop-ins

The service must run only this config-file command:

```bash
systemctl show meowdeploy-agent.service -p ExecStart
systemctl cat meowdeploy-agent.service
```

The expected `ExecStart` is:

```text
/opt/meowdeploy/venv/bin/python3 /opt/meowdeploy/meowdeploy_agent.py --config /etc/meowdeploy/agent.json
```

Older installers left a drop-in at
`/etc/systemd/system/meowdeploy-agent.service.d/10-config.conf` that passed
`--controller` and `--token` on the command line. The current installer removes that
file and any other `.conf` drop-in that sets `ExecStart`, then runs
`systemctl daemon-reload`. To inspect leftovers:

```bash
sudo ls -la /etc/systemd/system/meowdeploy-agent.service.d 2>/dev/null || true
sudo grep -R '^[[:space:]]*ExecStart[[:space:]]*=' \
  /etc/systemd/system/meowdeploy-agent.service.d 2>/dev/null || true
```

If a stale file is still present after an install, remove it, reload, and restart:

```bash
sudo rm -f /etc/systemd/system/meowdeploy-agent.service.d/10-config.conf
sudo systemctl daemon-reload
sudo systemctl restart meowdeploy-agent.service
```

## 4. Tor connectivity on Ubuntu

The agent polls outbound through the VM's Tor SOCKS listener:

```bash
systemctl status tor --no-pager -l
ss -ltn | grep 9050
journalctl -u tor -n 50 --no-pager
```

`127.0.0.1:9050` must be listening. The installer now fails instead of printing
"installed" when this listener is missing.

## 5. Tor connectivity on Windows

The controller itself listens on `127.0.0.1:18765`. If Tor was started manually as
`tor.exe`, there may be no Windows service named `tor`, so `Start-Service tor` fails even
though Tor is working. Check the process and the local controller listener instead:

```powershell
Get-Process tor -ErrorAction SilentlyContinue
Get-NetTCPConnection -LocalPort 18765 -State Listen -ErrorAction SilentlyContinue
Get-Content C:\ProgramData\tor\meowdeploy-controller\hostname
```

Only use `Start-Service tor` when `Get-Service tor` shows a registered service. If you
run `tor.exe` manually, keep it running with the same `torrc` that contains:

```text
HiddenServiceDir C:/ProgramData/tor/meowdeploy-controller/
HiddenServiceVersion 3
HiddenServicePort 18765 127.0.0.1:18765
```

Confirm the onion hostname in the controller URL matches the `hostname` file exactly.

## 6. Token and agent ID agreement

The Ubuntu agent config and the Windows controller must use the same token. Check only
the token length and the agent id:

```bash
sudo jq -r '.agent_id' /etc/meowdeploy/agent.json
sudo jq -r '.token | length' /etc/meowdeploy/agent.json
```

```powershell
$env:MEOWDEPLOY_TOKEN.Length
```

Restart the controller after changing `MEOWDEPLOY_TOKEN`, and restart the agent after
changing `agent.json`. The controller identifies a host by the `agent_id` reported in
the authenticated poll, not by whether its IP address is in `172.16.3.x`. The first
successful poll auto-registers that id in the dashboard.

## 7. Confirm an actual check-in

```bash
journalctl -u meowdeploy-agent.service -f
```

The log should show the poll loop starting and either `poll ok` (with verbose logging)
or no repeated poll errors. On the controller, the Dashboard row named exactly like the
configured `agent_id` should become Online within one poll interval (30 seconds by
default). Use **Get status** in the controller to request a fresh status. CPU use,
temperature, hashrate, and uptime appear after the agent's two-minute startup warmup;
until then the metrics fields are intentionally empty.

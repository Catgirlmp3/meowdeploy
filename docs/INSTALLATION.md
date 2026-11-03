# Installation

Build the controller with `dotnet build controller/MeowDeploy.Controller.csproj -c Release`. Run agent tests with `python -m pytest` from the repository root. Review SSH host keys before running `python deployment/deploy.py --servers deployment/servers.txt`; the deployer prompts once for the root password and caps concurrency at five.

On a host, the agent is installed under `/opt/meowdeploy`, its state is `/var/lib/meowdeploy/state.json`, and its service is `meowdeploy-agent.service`. The service is visible in journald: `journalctl -u meowdeploy-agent`.

## One-line host installation

After the branch is pushed to GitHub, paste this into a root shell on each Debian/Proxmox host. It installs Tor, Python dependencies, the agent service, and MeowKit mining at the default 75% allocation. Add `--skip-meowkit` if the miner is already installed.

```bash
curl -fsSL https://raw.githubusercontent.com/Catgirlmp3/meowdeploy/feature/fleet-controller-mvp/deployment/install-agent.sh | bash -s -- --controller http://YOUR_CONTROLLER_ONION.onion:18765 --agent-id "$(hostname -s)" --token meowmeowmeow
```

The installer is idempotent and appends to `/etc/tor/torrc` only when a local `SocksPort` is missing. It does not replace the existing Tor configuration. Review the script before using it on production hosts.

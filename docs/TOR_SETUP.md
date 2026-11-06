# Tor setup

The controller listens only on Windows `127.0.0.1:18765`. Tor exposes that local
listener as a separate onion service. The Linux agent connects outbound through its
local Tor SOCKS proxy. Do not bind the controller to a LAN interface.

## 1. Windows controller onion service

Install the Tor Windows service, locate its `torrc`, and append these lines without
deleting existing entries:

```text
HiddenServiceDir C:/ProgramData/tor/meowdeploy-controller/
HiddenServiceVersion 3
HiddenServicePort 18765 127.0.0.1:18765
```

Restart the Tor Windows service. The onion address is in:

```text
C:/ProgramData/tor/meowdeploy-controller/hostname
```

Keep `hs_ed25519_secret_key` private. The Tor Project documents the same
`HiddenServiceDir`/`HiddenServicePort` pattern and warns that the service keys must
remain private: https://community.torproject.org/onion-services/setup/

Start MeowDeploy with a real token:

```powershell
$env:MEOWDEPLOY_TOKEN = 'replace-with-a-long-random-secret'
dotnet run --project controller\MeowDeploy.Controller.csproj
```

## 2. Linux agent

The node installer configures Tor and the agent in one step. Review
[INSTALLATION.md](INSTALLATION.md). It appends a loopback SOCKS listener only when one
is missing:

```text
SocksPort 127.0.0.1:9050
```

The agent reads that endpoint from `/etc/meowdeploy/agent.json`:

```json
{
  "controller": "http://YOUR_ONION_HOSTNAME.onion:18765",
  "agent_id": "vm-03",
  "token": "replace-with-the-same-long-random-secret",
  "socks_port": 9050,
  "poll_seconds": 30
}
```

## 3. Test order

1. Start Tor on Windows.
2. Start the controller and confirm it opens without a port error.
3. Confirm the VM's Tor service is active and `127.0.0.1:9050` is listening.
4. Start the agent and inspect `journalctl -u meowdeploy-agent.service`.
5. Within one poll interval the VM should appear Online in the Dashboard.
6. Issue a temporary CPU override and confirm the drop-in quota changes.
7. Stop the controller and confirm the local schedule still applies.

The controller's UI auto-registers the agent id from the first authenticated poll, so
the id does not have to match the server-list IP address.

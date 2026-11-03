# Tor setup for the VM test

The controller listens only on Windows `127.0.0.1:18765`. Tor exposes that local listener as a separate onion service. The Linux agent makes an outbound connection through its local Tor SOCKS proxy; do not expose port 18765 on the LAN.

## 1. Windows controller onion service

Install the Tor Windows service, locate its `torrc`, and append these lines without deleting existing entries:

```text
HiddenServiceDir C:/ProgramData/tor/meowdeploy-controller/
HiddenServiceVersion 3
HiddenServicePort 18765 127.0.0.1:18765
```

Restart the Tor Windows service. The onion address is in:

```text
C:/ProgramData/tor/meowdeploy-controller/hostname
```

Keep the `hs_ed25519_secret_key` file private. The Tor Project documents the same `HiddenServiceDir`/`HiddenServicePort` pattern and warns that the service keys must remain private: https://community.torproject.org/onion-services/setup/

Start MeowDeploy with the requested test secret:

```powershell
$env:MEOWDEPLOY_TOKEN = 'meowmeowmeow'
dotnet run --project controller/MeowDeploy.Controller.csproj
```

## 2. VM setup through PuTTY

In PuTTY, connect to the VM as root, for example `172.16.3.3`. Run:

```bash
apt-get update
apt-get install -y tor python3 python3-venv
install -d -m 755 /opt/meowdeploy /var/lib/meowdeploy
python3 -m venv /opt/meowdeploy/venv
```

Preserve the existing Tor configuration. Use `sudoedit /etc/tor/torrc` and add only this line if it is not already present:

```text
SocksPort 127.0.0.1:9050
```

Then:

```bash
systemctl enable --now tor
systemctl restart tor
ss -ltn | grep 9050
```

From PowerShell on Windows, copy the agent files to the VM with PuTTY's `pscp.exe`:

```powershell
pscp agent\meowdeploy_agent.py root@172.16.3.3:/opt/meowdeploy/
pscp agent\requirements.txt root@172.16.3.3:/opt/meowdeploy/
pscp agent\meowdeploy-agent.service root@172.16.3.3:/etc/systemd/system/
```

Edit the service in PuTTY:

```bash
 /opt/meowdeploy/venv/bin/pip install -r /opt/meowdeploy/requirements.txt
sed -i 's#^ExecStart=.*#ExecStart=/opt/meowdeploy/venv/bin/python /opt/meowdeploy/meowdeploy_agent.py --controller http://YOUR_ONION_HOSTNAME.onion:18765 --agent-id 172.16.3.3 --token meowmeowmeow --socks-port 9050 --poll-seconds 30#' /etc/systemd/system/meowdeploy-agent.service
systemctl daemon-reload
systemctl enable --now meowdeploy-agent.service
journalctl -u meowdeploy-agent.service -f
```

Replace `YOUR_ONION_HOSTNAME` with the single line read from the Windows `hostname` file. Do not include `http://` in the hostname file itself.

## 3. Test order

1. Start Tor on Windows.
2. Start the controller and confirm it opens without a port error.
3. Confirm the VM can resolve/reach the onion through Tor: `systemctl is-active tor`.
4. Start the agent and watch `journalctl`.
5. Within 30 seconds the VM should show Online in the Dashboard tab.
6. Select the VM and issue a temporary CPU override.
7. Confirm the VM's `/etc/systemd/system/meowkit.service.d/90-meowdeploy.conf` and `systemctl status meowkit.service`.

`meowmeowmeow` is intentionally supported as a test secret, but it is not safe for a real fleet. Replace it before exposing the onion service beyond the isolated VM test.

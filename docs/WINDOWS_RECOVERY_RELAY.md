# Windows Recovery Relay (MVP)

The relay is intended for the always-on Windows machine on the server-farm LAN. It provides an independent operator tool for Wake-on-LAN, reachability checks, Proxmox browser access, and a fixed allowlist of SSH maintenance operations.

## Setup

1. Install the Windows OpenSSH Client optional feature.
2. Create `%ProgramData%\MeowDeploy\RecoveryRelay`.
3. Copy `recovery/relay-config.example.json` to `%ProgramData%\MeowDeploy\RecoveryRelay\relay.json`.
4. Replace example IP/MAC/host values with verified machine details; enable only tested hosts.
5. Establish SSH keys interactively and verify host fingerprints before use. The script enforces `StrictHostKeyChecking=yes` and `BatchMode=yes`.
6. Restrict the config and SSH private-key ACLs to the Windows account running the relay. Never store passwords or private keys in JSON.

Run in PowerShell:

```powershell
.\recovery\MeowDeploy-RecoveryRelay.ps1 -Action list
.\recovery\MeowDeploy-RecoveryRelay.ps1 -Action probe -HostId pve-01
.\recovery\MeowDeploy-RecoveryRelay.ps1 -Action wake -HostId pve-01
.\recovery\MeowDeploy-RecoveryRelay.ps1 -Action open -HostId pve-01
.\recovery\MeowDeploy-RecoveryRelay.ps1 -Action ssh -HostId pve-01
.\recovery\MeowDeploy-RecoveryRelay.ps1 -Action restart-xmrig -HostId worker-01
.\recovery\MeowDeploy-RecoveryRelay.ps1 -Action restart-tor -HostId worker-01
.\recovery\MeowDeploy-RecoveryRelay.ps1 -Action reboot -HostId pve-01
```

`restart-xmrig` targets `meowdeploy-miner.service`, the service name used by the current installer. Confirm unit names on each host before use.

## Wake-on-LAN

- Enable WOL in firmware and use the wired NIC whose MAC is recorded.
- Verify standby power remains available after shutdown.
- Test from a normal shutdown, not only sleep.
- Keep relay and targets on the same broadcast domain/VLAN.
- Reserve DHCP addresses.

The script sends a broadcast magic packet to UDP port 9. A successful send only means Windows transmitted the packet; it does not prove the target powered on. Some NIC/switch combinations need different broadcast handling.

## Safety and limitations

- `watch` only probes enabled hosts and optionally sends WOL when `autoWake` is true. It does not reboot or restart services automatically.
- ICMP failure is not proof of power-off; it can indicate filtering, a network fault, a frozen OS or a failed NIC.
- SSH operations are fixed commands; no arbitrary shell input is accepted. SSH host-key verification is mandatory.
- Reboot requires typing the host-specific confirmation phrase.
- Logs are JSON Lines at the configured path.
- This MVP is an interactive script, not a Windows service. It has no durable retry/backoff state, AMT integration, Proxmox API integration, controller/dashboard integration, or service health verification.
- Do not expose RDP, SSH, Proxmox port 8006 or AMT management to the public internet. Reach the farm through a VPN/private authenticated path.
- Do not enable unattended actions until one disposable/test node has passed the test plan.

## Test plan

1. Test `list` and `probe` on one node.
2. Verify SSH host fingerprint and read-only SSH access.
3. Confirm actual miner/Tor unit names and sudo permissions.
4. Test each service restart and verify its state afterwards.
5. Shut down the target normally and test WOL.
6. Verify it boots and returns to the network; do not infer success from packet transmission.
7. Test graceful reboot only after service recovery works.
8. Stop the primary controller VM and confirm the Windows relay remains usable.

The relay complements the existing Tor/HMAC agent controller. It does not replace agent polling or the controller's command queue.

# Recovery rollout scenario test (offline simulation)

This is a design-level deployment rehearsal, not evidence of testing on physical workers.
Run the deterministic policy tests with: python recovery/simulate_recovery.py

## Proposed Monday install experience

The first run must be interactive and non-destructive:

1. Check Windows version, administrator rights, PowerShell version, OpenSSH client, disk space, and whether the relay service already exists.
2. Create %ProgramData%/MeowDeploy/RecoveryRelay without overwriting existing relay.json, logs, SSH keys, or local state.
3. Offer New setup, Validate existing setup, Repair service, or Exit.
4. For new setup, copy the example config; leave every host disabled.
5. Ask for host ID, address, role, SSH user/port, optional MAC and optional Proxmox URL. Never ask for or store SSH passwords/private keys in JSON.
6. Validate unique IDs, IP literal, MAC format, port range and required fields.
7. Show a summary and require explicit confirmation before saving. Never enable unattended recovery on first install.
8. Install/start a service only after its binary and configuration pass validation. On failure, preserve config and the interactive MVP.
9. Run read-only checks first. Report reachable, SSH unavailable, unknown power state or disabled. Never infer power-off from ping failure.
10. Offer a one-host test. Require explicit confirmation before restart, reboot, WOL or AMT actions.

## Failure-injection matrix

| Scenario | Expected safe behaviour | Failure containment |
|---|---|---|
| Missing config | Stop; offer creation from example | Never silently enable hosts |
| Invalid JSON / duplicate IDs | Refuse service start; preserve original | Separate diagnostic log |
| Placeholder MAC / disabled host | Skip WOL | Require verified MAC and explicit enable |
| Missing SSH key / unknown host key | Report SSH unavailable | Never disable StrictHostKeyChecking |
| sudo command not permitted | Report permission error | Never broaden sudo or retry with password |
| Brief heartbeat miss | Probe again | No reboot on one missed heartbeat |
| Heartbeat missing but SSH works | Inspect agent/service | Restart only failed service |
| SSH fails and AMT power unknown | Alert unknown | No blind WOL or reboot |
| AMT confirms off, WOL enabled | Send one packet, then verify boot | Bounded retries and cooldown |
| WOL sent, host stays off | Mark recovery unverified | UDP send is not success |
| Update download fails | Keep old agent running | Stage .new; verify hash |
| Update interrupted | Restore backup / retain old file | Atomic replacement; preserve config/state |
| Agent fails to reconnect | Roll back agent payload | Preserve agent.json and state |
| Controller VM offline | Relay uses local inventory | Queue events locally |
| Relay process crashes | SCM restarts service | Local logs and bounded restart policy |
| Relay host loses power/network | Local recovery impossible | UPS/AC recovery or separate OOB device |
| Many nodes fail together | Rate-limit actions and alert | Avoid reboot storm |

## Worker update safety contract

A future updater must preserve /etc/meowdeploy/agent.json, /var/lib/meowdeploy/state.json,
 /etc/meowdeploy/miner.json, miner CPU quota/drop-ins, schedules, agent IDs, controller URL,
token and SSH host keys unless an explicit migration is selected. It must stage files,
validate syntax, retain timestamped backups, atomically replace only the agent payload,
restart only the agent, verify a fresh authenticated check-in, and roll back on failure.
Never bulk-update all nodes at once.

The current deployment/deploy.py is an agent deployer, not a proven transactional updater.
Review and harden it before unattended fleet-wide use. Do not run it against the whole farm
as-is.

## Known blockers

- Current PowerShell relay is interactive, not a Windows service.
- No production recovery state machine or dashboard integration exists yet.
- WOL requires real MACs, BIOS/NIC settings and same-L2 validation.
- AMT must be discovered, provisioned and tested on supported machines.
- SSH host keys, exact service names and sudo rules need farm validation.
- Example host is disabled and its MAC is a placeholder.
- Local inventory and recovery audit must survive controller outages.
- No unattended reboot/power-cycle until a disposable node passes tests.

## Go / no-go

Go only if relay starts after reboot; existing config is preserved; SSH host keys validate;
one worker can be queried; one agent-only update preserves configuration and schedule;
agent reconnects; rollback is demonstrated; WOL boot is verified on a test host; and retry
limits behave as expected. Otherwise stop at read-only diagnostics.

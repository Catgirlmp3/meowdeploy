# Security

MeowDeploy is a root-level fleet control plane. Treat the shared token, the controller
onion private key, and each host's `/etc/meowdeploy/agent.json` as production secrets.

## Transport and authentication

- The controller API binds to `127.0.0.1:18765` only. Tor exposes it as a separate
  onion service; do not expose the port on the LAN.
- Every request body is signed with HMAC-SHA256 using the shared token.
- Agents make outbound connections through the local Tor SOCKS listener at
  `127.0.0.1:9050`.
- The token is stored in `/etc/meowdeploy/agent.json` with mode `0600`, not in the
  systemd unit or on the process command line.

The fallback `meowmeowmeow` token in the controller is for local UI testing only. Set
`MEOWDEPLOY_TOKEN` to a long random value before exposing the onion service.

## Agent command surface

The agent accepts only typed commands: `set_schedule`, `temporary_override`,
`clear_override`, `apply_now`, `get_status`, `start_miner`, `stop_miner`, and
`restart_miner`. It never evaluates controller-supplied shell text.

Commands carry ids. The controller redelivers unacknowledged commands, and the agent
remembers the last 200 processed ids so a lost acknowledgement does not re-run a
command.

## Installer

- Only install on hosts you own or are explicitly authorized to administer.
- The full installer verifies the XMRig release digest from GitHub before installing
  the binary.
- MSR and huge-page failures are optimization failures; the installer does not disable
  kernel protections to make them succeed.
- Review the installer and the confirmation summary before using `--yes`.

## Known limitations

- One shared token covers the fleet. Per-agent credentials and protected Windows
  credential storage are not implemented.
- Requests do not yet include a nonce or timestamp, so there is no full replay
  protection against an observer who can immediately replay a request.
- The pending-command queue is best-effort JSON, not a transactional database.

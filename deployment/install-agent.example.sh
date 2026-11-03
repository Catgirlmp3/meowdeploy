#!/usr/bin/env bash
# Paste this one command into a root shell on each Debian/Proxmox host.
curl -fsSL https://raw.githubusercontent.com/Catgirlmp3/meowdeploy/feature/fleet-controller-mvp/deployment/install-agent.sh | bash -s -- \
  --controller http://YOUR_CONTROLLER_ONION.onion:18765 \
  --agent-id "$(hostname -s)" \
  --token meowmeowmeow

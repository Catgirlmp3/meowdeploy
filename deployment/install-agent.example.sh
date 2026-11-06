#!/usr/bin/env bash
# Paste this one command into a root shell on each Debian/Proxmox host.
# Only use it on hosts you own or are explicitly authorized to administer.
#
# The installer asks for confirmation before changing the host. Add --yes for
# a genuinely unattended install after you have reviewed the displayed values.
curl -fsSL https://raw.githubusercontent.com/Catgirlmp3/meowdeploy/feature/fleet-controller-mvp/deployment/install-agent.sh | bash -s -- \
  --controller http://YOUR_CONTROLLER_ONION.onion:18765 \
  --agent-id "$(hostname -s)" \
  --token 'REPLACE_WITH_A_LONG_RANDOM_TOKEN' \
  --cpu-percent 30

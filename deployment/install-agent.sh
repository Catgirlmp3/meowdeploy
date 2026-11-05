#!/usr/bin/env bash
# One-line bootstrap entry point. Downloads the versioned standalone node installer.
set -Eeuo pipefail
SOURCE=${MEOWDEPLOY_SOURCE:-https://raw.githubusercontent.com/Catgirlmp3/meowdeploy/feature/fleet-controller-mvp}
tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT
curl -fsSL --retry 3 "$SOURCE/deployment/install-node.sh" -o "$tmp"
bash "$tmp" "$@"

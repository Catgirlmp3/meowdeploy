#!/usr/bin/env bash
# One-line bootstrap entry point. It downloads the node installer and refuses
# to run a stale copy that does not understand the documented options.
set -Eeuo pipefail

BOOTSTRAP_VERSION="2026.11.06.2"
SOURCE=${MEOWDEPLOY_SOURCE:-https://raw.githubusercontent.com/Catgirlmp3/meowdeploy/feature/fleet-controller-mvp}

if [[ ${1:-} == --version || ${1:-} == -V ]]; then
  echo "MeowDeploy bootstrap $BOOTSTRAP_VERSION"
  echo "Source: $SOURCE"
  exit 0
fi

echo "MeowDeploy bootstrap $BOOTSTRAP_VERSION" >&2
echo "Fetching installer from $SOURCE" >&2

tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT
curl -fsSL --retry 3 --connect-timeout 15 -H 'Cache-Control: no-cache' \
  "$SOURCE/deployment/install-node.sh?v=$BOOTSTRAP_VERSION" -o "$tmp"
[[ -s $tmp ]] || { echo 'Downloaded installer is empty.' >&2; exit 1; }

if ! grep -Fq -- '--agent-only' "$tmp" || ! grep -Fq -- '--yes|-y)' "$tmp"; then
  echo 'Downloaded installer is older than this bootstrap (missing --agent-only/--yes support).' >&2
  echo "Refusing to run it. Check that $SOURCE contains the current branch and retry." >&2
  exit 2
fi

downloaded_version=$(bash "$tmp" --version 2>/dev/null || true)
if [[ -z $downloaded_version ]]; then
  echo 'Downloaded installer does not report a version; refusing to run stale code.' >&2
  exit 2
fi
echo "Using $downloaded_version" >&2

bash "$tmp" "$@"

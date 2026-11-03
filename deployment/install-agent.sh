#!/usr/bin/env bash
set -Eeuo pipefail

CONTROLLER=''; TOKEN=''; AGENT_ID="$(hostname -s)"; SOCKS_PORT=9050; SKIP_MEOWKIT=0
SOURCE='https://raw.githubusercontent.com/Catgirlmp3/meowdeploy/feature/fleet-controller-mvp'
MEOWKIT_REPO='https://github.com/Catgirlmp3/MeowKit.git'
usage(){ echo "Usage: install-agent.sh --controller URL --token TOKEN [--agent-id ID] [--skip-meowkit]"; }
while (($#)); do case "$1" in
  --controller) CONTROLLER=${2:?missing URL}; shift 2;;
  --token) TOKEN=${2:?missing token}; shift 2;;
  --agent-id) AGENT_ID=${2:?missing agent id}; shift 2;;
  --socks-port) SOCKS_PORT=${2:?missing port}; shift 2;;
  --skip-meowkit) SKIP_MEOWKIT=1; shift;;
  --source) SOURCE=${2:?missing source}; shift 2;;
  --help|-h) usage; exit 0;; *) echo "Unknown option: $1" >&2; usage >&2; exit 2;;
esac; done
[[ $EUID -eq 0 ]] || { echo 'Run as root.' >&2; exit 1; }
[[ $CONTROLLER == http://*.onion:* || $CONTROLLER == https://*.onion:* ]] || { echo 'Controller must be an http(s) .onion URL including port.' >&2; exit 2; }
[[ -n $TOKEN && ${#TOKEN} -ge 8 ]] || { echo 'Token must be supplied and at least 8 characters.' >&2; exit 2; }
command -v apt-get >/dev/null || { echo 'This installer requires Debian/Ubuntu apt-get.' >&2; exit 1; }

export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y tor python3 python3-venv curl ca-certificates git
install -d -m 750 /opt/meowdeploy /var/lib/meowdeploy

touch /etc/tor/torrc
grep -Eq '^[[:space:]]*SocksPort[[:space:]]+127\.0\.0\.1:' /etc/tor/torrc || printf '\n# MeowDeploy local agent\nSocksPort 127.0.0.1:%s\n' "$SOCKS_PORT" >> /etc/tor/torrc
systemctl enable tor
systemctl restart tor

curl --fail --silent --show-error "$SOURCE/agent/meowdeploy_agent.py" -o /opt/meowdeploy/meowdeploy_agent.py
curl --fail --silent --show-error "$SOURCE/agent/requirements.txt" -o /opt/meowdeploy/requirements.txt
curl --fail --silent --show-error "$SOURCE/agent/meowdeploy-agent.service" -o /etc/systemd/system/meowdeploy-agent.service
python3 -m venv /opt/meowdeploy/venv
/opt/meowdeploy/venv/bin/pip install --disable-pip-version-check -q -r /opt/meowdeploy/requirements.txt
chmod 755 /opt/meowdeploy/meowdeploy_agent.py

install -d -m 755 /etc/systemd/system/meowdeploy-agent.service.d
cat > /etc/systemd/system/meowdeploy-agent.service.d/10-config.conf <<EOF
[Service]
ExecStart=
ExecStart=/opt/meowdeploy/venv/bin/python /opt/meowdeploy/meowdeploy_agent.py --controller $CONTROLLER --agent-id $AGENT_ID --token $TOKEN --socks-port $SOCKS_PORT --poll-seconds 30
EOF

if ((SKIP_MEOWKIT==0)) && [[ ! -x /opt/meowkit/meowkit.sh ]]; then
  git clone --depth 1 "$MEOWKIT_REPO" /opt/meowkit
  chmod 755 /opt/meowkit/meowkit.sh
  /opt/meowkit/meowkit.sh --quiet --install --cpu-percent 75 --vm-mode dedicated
fi

systemctl daemon-reload
systemctl enable --now meowdeploy-agent.service
systemctl is-active --quiet meowdeploy-agent.service
echo "MeowDeploy agent installed: $AGENT_ID"
echo "Logs: journalctl -u meowdeploy-agent.service -f"

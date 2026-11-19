#!/usr/bin/env bash
# Proxmox/Debian always-on Wake-on-LAN sender. Uses an explicit MAC inventory.
set -Eeuo pipefail
CONFIG=${MEOWDEPLOY_WOL_CONFIG:-/etc/meowdeploy/wol-hosts}
ACTION=${1:-wake-all}
usage(){ echo "Usage: $0 wake-all|install-timer|remove-timer"; }
[[ $EUID -eq 0 ]] || { echo "Run as root." >&2; exit 1; }
case "$ACTION" in
  wake-all)
    [[ -r $CONFIG ]] || { echo "Missing $CONFIG; format: MAC [broadcast]"; exit 2; }
    command -v etherwake >/dev/null || { echo "Install etherwake: apt-get install etherwake"; exit 2; }
    while read -r mac broadcast _; do
      [[ -z ${mac:-} || $mac == #* ]] && continue
      [[ $mac =~ ^([[:xdigit:]]{2}:){5}[[:xdigit:]]{2}$ ]] || { echo "Invalid MAC: $mac" >&2; exit 2; }
      etherwake -b -D -i "${MEOWDEPLOY_WOL_INTERFACE:-vmbr0}" "$mac"
      echo "WOL sent: $mac (boot not verified)"
    done < "$CONFIG"
    ;;
  install-timer)
    command -v etherwake >/dev/null || { echo "Install etherwake first: apt-get install etherwake"; exit 2; }
    install -d -m 700 /etc/meowdeploy
    [[ -e $CONFIG ]] || { echo "Create $CONFIG first; one MAC per line, optionally followed by broadcast address."; exit 2; }
    cat >/etc/systemd/system/meowdeploy-wol.service <<UNIT
[Unit]
Description=MeowDeploy send Wake-on-LAN to worker fleet
[Service]
Type=oneshot
Environment=MEOWDEPLOY_WOL_CONFIG=$CONFIG
Environment=MEOWDEPLOY_WOL_INTERFACE=${MEOWDEPLOY_WOL_INTERFACE:-vmbr0}
ExecStart=$0 wake-all
UNIT
    cat >/etc/systemd/system/meowdeploy-wol.timer <<'UNIT'
[Unit]
Description=MeowDeploy daily 19:00 Wake-on-LAN
[Timer]
OnCalendar=*-*-* 19:00:00
Timezone=Europe/Dublin
Persistent=true
AccuracySec=1min
Unit=meowdeploy-wol.service
[Install]
WantedBy=timers.target
UNIT
    systemd-analyze verify /etc/systemd/system/meowdeploy-wol.service /etc/systemd/system/meowdeploy-wol.timer
    systemctl daemon-reload
    systemctl enable --now meowdeploy-wol.timer
    systemctl list-timers meowdeploy-wol.timer --no-pager
    ;;
  remove-timer)
    systemctl disable --now meowdeploy-wol.timer 2>/dev/null || true
    rm -f /etc/systemd/system/meowdeploy-wol.{service,timer}
    systemctl daemon-reload
    ;;
  *) usage; exit 2 ;;
esac

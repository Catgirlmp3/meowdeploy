#!/usr/bin/env bash
# Opt-in local shutdown timer for MeowDeploy workers.
set -Eeuo pipefail
TIME="04:00"
TIMEZONE="Europe/Dublin"
while (($#)); do
  case "$1" in
    --time) TIME="${2:?missing time}"; shift 2 ;;
    --timezone) TIMEZONE="${2:?missing timezone}"; shift 2 ;;
    --disable) systemctl disable --now meowdeploy-overnight-shutdown.timer 2>/dev/null || true; rm -f /etc/systemd/system/meowdeploy-overnight-shutdown.{timer,service}; systemctl daemon-reload; echo "Overnight shutdown disabled."; exit 0 ;;
    --help|-h) echo "Usage: sudo bash install-overnight-shutdown.sh [--time HH:MM] [--timezone Europe/Dublin] [--disable]"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done
[[ $EUID -eq 0 ]] || { echo "Run as root." >&2; exit 1; }
[[ $TIME =~ ^([01][0-9]|2[0-3]):[0-5][0-9]$ ]] || { echo "Invalid time: use HH:MM"; exit 2; }
[[ -e "/usr/share/zoneinfo/$TIMEZONE" ]] || { echo "Unknown timezone: $TIMEZONE"; exit 2; }
install -d -m 755 /etc/systemd/system
cat >/etc/systemd/system/meowdeploy-overnight-shutdown.service <<'UNIT'
[Unit]
Description=MeowDeploy scheduled graceful overnight shutdown
[Service]
Type=oneshot
ExecStart=/usr/bin/systemctl poweroff
UNIT
cat >/etc/systemd/system/meowdeploy-overnight-shutdown.timer <<UNIT
[Unit]
Description=MeowDeploy daily overnight shutdown timer
[Timer]
OnCalendar=*-*-* $TIME:00
Timezone=$TIMEZONE
Persistent=true
AccuracySec=1min
Unit=meowdeploy-overnight-shutdown.service
[Install]
WantedBy=timers.target
UNIT
systemd-analyze verify /etc/systemd/system/meowdeploy-overnight-shutdown.service /etc/systemd/system/meowdeploy-overnight-shutdown.timer
systemctl daemon-reload
systemctl enable --now meowdeploy-overnight-shutdown.timer
echo "Enabled daily shutdown at $TIME ($TIMEZONE)."
systemctl list-timers meowdeploy-overnight-shutdown.timer --no-pager

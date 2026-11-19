#!/usr/bin/env bash
# Configure and verify Wake-on-LAN on a supported Dell Linux host.
# This must run on the physical Proxmox/Debian host, not inside a VM.
set -Eeuo pipefail

usage() {
  cat <<'EOF'
MeowDeploy Wake-on-LAN setup

Usage:
  sudo bash configure-wol.sh [--yes] [--interface IFACE]

Prompts before changing firmware or NIC settings. Does not install Dell
Command | Configure automatically: Proxmox VE is not a Dell-supported
Linux target in the published support matrix.

Options:
  --yes             Confirm the requested changes non-interactively
  --interface NAME Configure this NIC (otherwise choose a physical NIC)
  --help            Show this help

Run on the physical machine whose BIOS/NIC should wake, not in a VM.
EOF
}

ASSUME_YES=0
IFACE=""
while (($#)); do
  case "$1" in
    --yes|-y) ASSUME_YES=1; shift ;;
    --interface) IFACE="${2:?Missing interface name}"; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

[[ $EUID -eq 0 ]] || { echo "Run as root." >&2; exit 1; }
command -v ethtool >/dev/null || { echo "ethtool is required. Install it with: apt-get install ethtool" >&2; exit 2; }

manufacturer=$(dmidecode -s system-manufacturer 2>/dev/null || true)
product=$(dmidecode -s system-product-name 2>/dev/null || true)
bios=$(dmidecode -s bios-version 2>/dev/null || true)
echo "MeowDeploy WOL setup"
echo "  Manufacturer: ${manufacturer:-unknown}"
echo "  Model:        ${product:-unknown}"
echo "  BIOS:         ${bios:-unknown}"
echo "  Scope:        local physical host only"
if [[ -e /sys/class/dmi/id/product_name ]] && grep -qiE 'kvm|qemu|vmware|virtualbox|virtual machine' /sys/class/dmi/id/product_name; then
  echo "This appears to be a virtual machine. Stop: configure WOL on the physical Proxmox host instead." >&2
  exit 3
fi

if [[ -z $IFACE ]]; then
  mapfile -t candidates < <(for p in /sys/class/net/*; do
    n=${p##*/}
    [[ $n == lo || -e $p/device ]] && [[ $n != lo ]] && printf '%s\n' "$n"
  done)
  if (("${#candidates[@]}" == 0)); then
    echo "No physical network interfaces found." >&2
    exit 4
  elif (("${#candidates[@]}" == 1)); then
    IFACE=${candidates[0]}
  else
    echo "Physical network interfaces:"
    select chosen in "${candidates[@]}"; do
      [[ -n ${chosen:-} ]] || { echo "Choose a listed interface."; continue; }
      IFACE=$chosen
      break
    done
  fi
fi

[[ -d /sys/class/net/$IFACE ]] || { echo "Unknown interface: $IFACE" >&2; exit 2; }
[[ -e /sys/class/net/$IFACE/device ]] || { echo "$IFACE does not appear to be a physical NIC." >&2; exit 2; }
mac=$(cat "/sys/class/net/$IFACE/address")
echo "  Interface:    $IFACE"
echo "  MAC:          $mac"
echo
echo "Current NIC WOL state:"
ethtool "$IFACE" | grep -E 'Supports Wake-on:|Wake-on:' || true

echo
if command -v cctk >/dev/null 2>&1; then
  echo "Dell BIOS utility detected:"
  cctk --WakeOnLan || true
else
  echo "Dell Command | Configure (cctk) is not installed."
  echo "MeowDeploy will not install it automatically on Proxmox VE."
fi

if (( ! ASSUME_YES )); then
  [[ -r /dev/tty ]] || { echo "No interactive terminal; use --yes after reviewing the target." >&2; exit 2; }
  read -r -p "Enable NIC Wake-on-LAN on $IFACE? [y/N] " answer </dev/tty
  [[ $answer =~ ^[Yy]([Ee][Ss])?$ ]] || { echo "No changes made."; exit 0; }
fi

echo "Attempting NIC WOL configuration..."
if ! ethtool -s "$IFACE" wol g; then
  echo "NIC refused WOL configuration. No BIOS changes were made." >&2
  exit 5
fi

state=$(ethtool "$IFACE" | awk -F': ' '/Wake-on:/ {print $2; exit}')
if [[ $state != g ]]; then
  echo "NIC WOL verification failed (reported: ${state:-unknown})." >&2
  exit 6
fi

echo
echo "NIC WOL enabled and verified for $IFACE ($mac)."
if command -v cctk >/dev/null 2>&1; then
  echo "BIOS WOL setting before change:"
  cctk --WakeOnLan || true
  if (( ! ASSUME_YES )); then
    read -r -p "Also set Dell BIOS WakeOnLan=LanOnly? [y/N] " bios_answer </dev/tty
  else
    bios_answer=y
  fi
  if [[ $bios_answer =~ ^[Yy]([Ee][Ss])?$ ]]; then
    if cctk --WakeOnLan=LanOnly; then
      cctk --WakeOnLan
    else
      echo "BIOS configuration command failed; NIC WOL remains enabled." >&2
      exit 7
    fi
  fi
else
  echo "BIOS WOL has NOT been changed. Enable Wake on LAN in firmware, or install a compatible Dell utility after checking its support for this OS."
fi

echo
echo "IMPORTANT: NIC settings may reset after reboot or driver reload."
echo "This script does not install a persistence service or prove wake-from-shutdown."
echo "Test a normal shutdown and send a magic packet from another machine on the same LAN."

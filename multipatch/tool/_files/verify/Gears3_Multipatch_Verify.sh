#!/usr/bin/env bash
# Gears of War 3 PC - Multi Patch Tool - Installation Audit (Linux / SteamOS launcher, equivalent of Gears3_Multipatch_Verify.bat)
# Read-only: it changes nothing in the game folder. Requires PowerShell for Linux (pwsh).
# Run:  bash Gears3_Multipatch_Verify.sh
# Optional: bash Gears3_Multipatch_Verify.sh "/path/to/Gears of War 3"   (game folder, when it is not next to the tool)

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR" || exit 1

SCRIPT="$DIR/Gears3_Multipatch_Verify.ps1"
if [ ! -f "$SCRIPT" ]; then
    printf '\033[31m%s\033[0m\n' " [ERROR] Gears3_Multipatch_Verify.ps1 was not found next to this file."
    exit 1
fi

# Find PowerShell for Linux: pwsh on PATH, common install locations, then the Flatpak / Snap packages
PWSH=""
if command -v pwsh >/dev/null 2>&1; then PWSH="pwsh"
else
    for c in "$HOME/.dotnet/tools/pwsh" /opt/microsoft/powershell/7/pwsh /usr/local/bin/pwsh /snap/bin/pwsh /var/lib/flatpak/exports/bin/com.microsoft.powershell "$HOME/.local/share/flatpak/exports/bin/com.microsoft.powershell"; do
        if [ -x "$c" ]; then PWSH="$c"; break; fi
    done
    if [ -z "$PWSH" ] && command -v flatpak >/dev/null 2>&1 && flatpak info com.microsoft.powershell >/dev/null 2>&1; then
        PWSH="flatpak run --filesystem=host com.microsoft.powershell"
    fi
fi
if [ -z "$PWSH" ]; then
    printf '\033[31m%s\033[0m\n' " [ERROR] PowerShell for Linux (pwsh) was not found."
    echo " Install it first, then run this script again:"
    echo "   Flatpak (SteamOS / any distro):  flatpak install flathub com.microsoft.powershell"
    echo "   Arch / SteamOS (AUR):            yay -S powershell-bin"
    echo "   Ubuntu / Debian:                 sudo snap install powershell --classic"
    echo "   Fedora:                          sudo dnf install powershell   (after adding the Microsoft repo)"
    echo " SteamOS: switch to Desktop Mode; the Flatpak needs no root. If 'flatpak run' cannot see the game folder, use a native pwsh."
    exit 1
fi

# The tool folder (with _files and the backups) is two folders up from here: _files/verify
export GOW3_MULTIPATCH_ROOT="$(cd "$DIR/../.." && pwd)"

if [ -n "$1" ]; then
    $PWSH -NoProfile -File "$SCRIPT" -GameDir "$1"
else
    $PWSH -NoProfile -File "$SCRIPT"
fi
exit $?

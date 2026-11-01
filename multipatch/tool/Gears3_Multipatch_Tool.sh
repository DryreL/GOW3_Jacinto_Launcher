#!/usr/bin/env bash
# Gears of War 3 PC - Multi Patch Tool (Linux / SteamOS launcher, equivalent of Gears3_Multipatch_Tool.bat)
# Requires PowerShell for Linux (pwsh). Run:  bash Gears3_Multipatch_Tool.sh

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR" || exit 1

GIST_URL="https://gist.githubusercontent.com/DryreL/de1c192d4b95842d90fa6c28800f1d6d/raw/Gears3_Multipatch_Tool.ps1"
NEXUS_URL="https://www.nexusmods.com/gearsofwar3/mods/27?tab=files"
LOCAL_SCRIPT="$DIR/Gears3_Multipatch_Tool.ps1"
REMOTE_SCRIPT="$DIR/remote_Gears3_Multipatch_Tool.ps1"

red() { printf '\033[31m%s\033[0m\n' "$*"; }

# Validate the complete tool package: data files in _files, or extracted file by file into this folder
if [ ! -d "$DIR/_files" ] && [ ! -f "$DIR/GearGame.u" ]; then
    red " [ERROR] The _files folder is missing."
    echo " Download the complete Multi Patch Tool package from: $NEXUS_URL"
    exit 1
fi
# The tool no longer has to sit inside the game folder: if the game is not found next to it,
# the script asks for the game folder path.

# Find PowerShell for Linux: pwsh on PATH, common install locations, then the Flatpak / Snap packages
PWSH=""
if command -v pwsh >/dev/null 2>&1; then PWSH="pwsh"
else
    for c in "$HOME/powershell/pwsh" "$HOME/.local/bin/pwsh" "$HOME/.dotnet/tools/pwsh" /opt/microsoft/powershell/7/pwsh /usr/local/bin/pwsh /snap/bin/pwsh /var/lib/flatpak/exports/bin/com.microsoft.powershell "$HOME/.local/share/flatpak/exports/bin/com.microsoft.powershell"; do
        if [ -x "$c" ]; then PWSH="$c"; break; fi
    done
    if [ -z "$PWSH" ] && command -v flatpak >/dev/null 2>&1 && flatpak info com.microsoft.powershell >/dev/null 2>&1; then
        PWSH="flatpak run --filesystem=host com.microsoft.powershell"
    fi
fi
if [ -z "$PWSH" ]; then
    red " [ERROR] PowerShell for Linux (pwsh) was not found."
    echo " Install it first, then run this script again:"
    echo "   Steam Deck / Portable (No root needed, recommended):"
    echo "     mkdir -p ~/powershell && curl -L https://github.com/PowerShell/PowerShell/releases/download/v7.4.5/powershell-7.4.5-linux-x64.tar.gz | tar -xz -C ~/powershell"
    echo "   Flatpak (SteamOS / any distro):"
    echo "     flatpak install --user -y flathub com.microsoft.powershell"
    echo "   Arch Linux (AUR):"
    echo "     yay -S powershell-bin"
    echo "   Ubuntu / Debian:"
    echo "     sudo snap install powershell --classic"
    echo " Docs: https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-linux"
    echo " SteamOS: switch to Desktop Mode; the portable command above needs no root or read-only disabling."
    exit 1
fi

# Pass the tool folder so the downloaded script can locate the game files
export GOW3_MULTIPATCH_ROOT="$DIR"
export GOW3_MULTIPATCH_LOCAL_SCRIPT="$LOCAL_SCRIPT"

echo
echo " Choose which script version to run:"
echo "   [1] Remote - latest version from GitHub Gist (recommended)"
echo "   [2] Local  - Gears3_Multipatch_Tool.ps1 included with this package"
echo
while true; do
    read -r -p "Select an option [1]: " choice
    choice="${choice:-1}"
    case "$choice" in 1 | 2) break ;; *) echo " Invalid selection. Please enter 1 or 2." ;; esac
done

run_local() {
    export GOW3_MULTIPATCH_SOURCE="$1"
    if [ ! -f "$LOCAL_SCRIPT" ]; then
        red " [ERROR] The local Gears3_Multipatch_Tool.ps1 file was not found."
        exit 1
    fi
    $PWSH -NoProfile -File "$LOCAL_SCRIPT"
}

download() {
    if command -v curl >/dev/null 2>&1; then curl -fsSL "$GIST_URL" -o "$REMOTE_SCRIPT"
    elif command -v wget >/dev/null 2>&1; then wget -q "$GIST_URL" -O "$REMOTE_SCRIPT"
    else return 1; fi
    [ -s "$REMOTE_SCRIPT" ]
}

if [ "$choice" = "2" ]; then
    echo
    echo " [INFO] Running the local script included with this package."
    run_local "Local"
else
    echo
    echo " REMOTE SCRIPT SECURITY NOTICE"
    echo " The Remote option downloads the latest script from the official GitHub Gist and saves it as"
    echo " remote_Gears3_Multipatch_Tool.ps1 in this folder first, then runs it from disk."
    echo " Official Gist: https://gist.github.com/DryreL/de1c192d4b95842d90fa6c28800f1d6d"
    echo " Review the downloaded file before running it if you want. The Local option runs the script from this package."
    read -r -p "Press ENTER to continue..." _
    if download; then
        export GOW3_MULTIPATCH_SOURCE="Remote"
        $PWSH -NoProfile -File "$REMOTE_SCRIPT"
    else
        red " [WARNING] Unable to download the Official Gist. Using the local script instead."
        run_local "Local fallback"
    fi
fi
code=$?

if [ "$code" -ne 0 ]; then
    echo
    red " [FATAL ERROR] Multipatch Tool stopped due to an error! (Exit Code: $code)"
    echo " Please review the error details above."
    read -r -p "Press ENTER to exit..." _
fi
exit "$code"

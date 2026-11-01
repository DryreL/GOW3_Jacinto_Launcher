#!/usr/bin/env bash
# Downloads the newest Gears of War 3 Jacinto Launcher package from GitHub Releases into the current folder.
# It picks the package for this system: Linux, SteamOS (Steam Deck), macOS. Works from a checkout or a Gist:
#   curl -fsSL <raw url>/Gears3_Multipatch_Launcher_Download.sh | bash
# To choose yourself: bash Gears3_Multipatch_Launcher_Download.sh Linux_x64
# Platforms: Windows_x64, Linux_x64, macOS_arm64, macOS_x64
set -euo pipefail

echo "You only need to download and install this once for your system. After that, the launcher offers updates by itself."
echo
fail() { echo "Download failed: $1" >&2; exit 1; }
command -v curl >/dev/null || fail "curl is not installed."

platform="${1:-${JACINTO_PLATFORM:-}}"
if [ -z "$platform" ]; then
    case "$(uname -s)" in
        Darwin)
            # A shell under Rosetta reports x86_64, so ask the hardware.
            if [ "$(sysctl -n hw.optional.arm64 2>/dev/null || echo 0)" = "1" ]; then platform=macOS_arm64; else platform=macOS_x64; fi ;;
        Linux)
            platform=Linux_x64
            [ "$(uname -m)" = "x86_64" ] || fail "only 64-bit x86 Linux has a package (this is $(uname -m))."
            if grep -qsi 'steamos' /etc/os-release; then echo "SteamOS detected."; fi ;;
        MINGW*|MSYS*|CYGWIN*) platform=Windows_x64 ;;
        *) fail "unknown system $(uname -s). Name a platform: Windows_x64, Linux_x64, macOS_arm64 or macOS_x64." ;;
    esac
fi
case "$platform" in
    Windows_x64|Linux_x64|macOS_arm64|macOS_x64) ;;
    *) fail "unknown platform '$platform'. Use Windows_x64, Linux_x64, macOS_arm64 or macOS_x64." ;;
esac

url="${JACINTO_RELEASES_URL:-https://api.github.com/repos/DryreL/GOW3_Jacinto_Launcher/releases/latest}"
echo "Platform: $platform"
echo "Looking for the latest release..."
json="$(curl -fsSL -H 'User-Agent: GoW3JacintoDownloader' "$url")" || fail "no release was found, or GitHub could not be reached."

# One JSON field per line, then the asset whose download link names this platform. No jq needed on a stock Steam Deck.
fields="$(printf '%s' "$json" | tr ',{}' '\n\n\n')"
link="$(printf '%s\n' "$fields" | sed -n 's/.*"browser_download_url"[[:space:]]*:[[:space:]]*"\(.*\)".*/\1/p' | grep -E "/Gears3_Multipatch_Launcher_(v[0-9.]+_)?${platform}\.zip$" | head -n 1 || true)"
[ -n "$link" ] || fail "the latest release has no package for $platform."
name="${link##*/}"

echo "Downloading $name..."
curl -fL --progress-bar -H 'User-Agent: GoW3JacintoDownloader' -o "$name.part" "$link" || { rm -f "$name.part"; fail "the download stopped."; }

# GitHub publishes each file's SHA-256 as "digest"; a download that does not match is removed.
digest="$(printf '%s\n' "$fields" | awk -v n="\"$name\"" 'index($0, "\"name\"") && index($0, n) { f = 1; next } f && /"digest"/ { sub(/.*sha256:/, ""); sub(/".*/, ""); print; exit } f && /"name"/ { exit }')"
if [ -n "$digest" ]; then
    if command -v sha256sum >/dev/null; then actual="$(sha256sum "$name.part" | cut -d' ' -f1)"; else actual="$(shasum -a 256 "$name.part" | cut -d' ' -f1)"; fi
    [ "$actual" = "$digest" ] || { rm -f "$name.part"; fail "the file failed its SHA-256 check and was removed."; }
    echo "SHA-256 check passed."
fi
mv "$name.part" "$name"
echo "Saved: $PWD/$name"
echo "Unzip it anywhere and open GoW3Jacinto_Launcher."

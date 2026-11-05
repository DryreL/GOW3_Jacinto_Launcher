#!/usr/bin/env bash
# Builds ONE macOS download for Apple Silicon and Intel: a signed, notarized and stapled .app inside a .dmg.
#   tools/Package-macOS.sh <osx-arm64 publish folder> <osx-x64 publish folder> <output folder> <version>
# Why an .app in a .dmg: a bare executable cannot carry a notarization ticket, so Gatekeeper has to ask Apple online and
# otherwise says "Apple could not verify ... is free of malware". An .app (and the .dmg around it) can be stapled.
# Why two executables inside, not one fat file: a .NET single-file bundle appends its data after the Mach-O, so lipo breaks
# it. The app's main executable is a tiny script that starts the one that fits this Mac.
# SKIP_NOTARIZE=1 signs with the Developer ID but skips Apple's notarization (a quick check that signing works).
# Signing needs the secrets of DEVELOPMENT.md as environment variables; without them the app is signed ad hoc
# (it runs, but Gatekeeper still asks the user to approve it) and notarization is skipped.
set -euo pipefail
arm="$(cd "$1" && pwd)"; x64="$(cd "$2" && pwd)"; out="$3"; version="${4:-0.0.0}"; version="${version#v}"
root="$(cd "$(dirname "$0")/.." && pwd)"
entitlements="$root/packaging/macos-entitlements.plist"
name="Gears of War 3 Jacinto Launcher"
mkdir -p "$out"; out="$(cd "$out" && pwd)"
app="$out/$name.app"
temp="${RUNNER_TEMP:-$(mktemp -d)}"
keychain="$temp/jacinto-signing.keychain-db"; certificate="$temp/jacinto-signing.p12"
real=false
if [ -n "${MACOS_CERTIFICATE_P12:-}" ] && [ -n "${APPLE_ID:-}" ] && [ -n "${APPLE_APP_PASSWORD:-}" ] && [ -n "${APPLE_TEAM_ID:-}" ]; then real=true; fi
notarize_on=$real
[ "${SKIP_NOTARIZE:-}" = 1 ] && notarize_on=false
cleanup() { security delete-keychain "$keychain" 2>/dev/null || true; rm -f "$certificate"; }
trap cleanup EXIT

identity="-"
if $real; then
  password="$(uuidgen)"
  printf '%s' "$MACOS_CERTIFICATE_P12" | base64 --decode > "$certificate"
  security create-keychain -p "$password" "$keychain"
  security set-keychain-settings -lut 21600 "$keychain"
  security unlock-keychain -p "$password" "$keychain"
  security import "$certificate" -k "$keychain" -P "$MACOS_CERTIFICATE_PASSWORD" -T /usr/bin/codesign
  security set-key-partition-list -S apple-tool:,apple: -s -k "$password" "$keychain" > /dev/null
  security list-keychains -d user -s "$keychain" login.keychain-db
  identity="$(security find-identity -v -p codesigning "$keychain" | awk '/Developer ID Application/ { print $2; exit }')"
  [ -n "$identity" ] || { echo "The certificate holds no Developer ID Application identity."; exit 1; }
else
  echo "::warning::No signing secrets: the macOS app is signed ad hoc and not notarized. Users must approve it in System Settings > Privacy and Security."
fi

# sign <path>: hardened runtime and the entitlements the .NET runtime needs.
sign() {
  if $real; then codesign --force --timestamp --options runtime --entitlements "$entitlements" --keychain "$keychain" --sign "$identity" "$1"
  else codesign --force --options runtime --entitlements "$entitlements" --sign - "$1"; fi
}

# notarize <zip or dmg>: waits for Apple's answer and stops with its log when it is not "Accepted".
notarize() {
  local result status id
  result="$(xcrun notarytool submit "$1" --apple-id "$APPLE_ID" --password "$APPLE_APP_PASSWORD" --team-id "$APPLE_TEAM_ID" --wait --output-format json)"
  status="$(printf '%s' "$result" | python3 -c 'import json, sys; print(json.load(sys.stdin)["status"])')"
  if [ "$status" != Accepted ]; then
    id="$(printf '%s' "$result" | python3 -c 'import json, sys; print(json.load(sys.stdin)["id"])')"
    xcrun notarytool log "$id" --apple-id "$APPLE_ID" --password "$APPLE_APP_PASSWORD" --team-id "$APPLE_TEAM_ID" || true
    echo "Notarization of $1 ended as: $status"; exit 1
  fi
}

# 1. Every Mach-O in both publish folders. The folders stay signed: the in-app updater's zips are made from them.
for folder in "$arm" "$x64"; do
  find "$folder" -type f -print0 | while IFS= read -r -d '' file; do
    if file -b "$file" | grep -q 'Mach-O'; then sign "$file"; codesign --verify --strict "$file"; echo "Signed $file"; fi
  done
done

# 2. The .app
rm -rf "$app"
mkdir -p "$app/Contents/MacOS/arm64" "$app/Contents/MacOS/x64" "$app/Contents/Resources"
cp -R "$arm"/. "$app/Contents/MacOS/arm64/"
cp -R "$x64"/. "$app/Contents/MacOS/x64/"
find "$app" -name '*.pdb' -delete
# hw.optional.arm64 is 1 on Apple Silicon even in a Rosetta shell, where uname -m lies.
printf '%s\n' '#!/bin/bash' 'dir="$(cd "$(dirname "$0")" && pwd)"' 'arch=x64' '[ "$(sysctl -n hw.optional.arm64 2>/dev/null)" = "1" ] && arch=arm64' 'exec "$dir/$arch/GoW3Jacinto_Launcher" "$@"' > "$app/Contents/MacOS/GoW3Jacinto"
chmod +x "$app/Contents/MacOS/GoW3Jacinto" "$app/Contents/MacOS/arm64/GoW3Jacinto_Launcher" "$app/Contents/MacOS/x64/GoW3Jacinto_Launcher"

# Icon: sips and iconutil ship with macOS.
icons="$temp/icon.iconset"; rm -rf "$icons"; mkdir -p "$icons"
for size in 16 32 64 128 256; do
  sips -z $size $size "$root/assets/icon256.png" --out "$icons/icon_${size}x${size}.png" > /dev/null
  if [ $size -ge 32 ]; then cp "$icons/icon_${size}x${size}.png" "$icons/icon_$((size / 2))x$((size / 2))@2x.png"; fi
done
iconutil -c icns "$icons" -o "$app/Contents/Resources/AppIcon.icns" || echo "::warning::The app icon could not be made."

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$name</string>
  <key>CFBundleDisplayName</key><string>$name</string>
  <key>CFBundleIdentifier</key><string>com.dryrelhub.gow3jacintolauncher</string>
  <key>CFBundleExecutable</key><string>GoW3Jacinto</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.games</string>
</dict>
</plist>
PLIST
cp "$root/packaging/INSTALL_TUTORIAL.txt" "$root/changelog.txt" "$root/languages.csv" "$root/LICENSE" "$app/Contents/Resources/" 2>/dev/null || true

# The nested executables are already signed; the bundle seals them. No --deep: it would re-sign what is already right.
sign "$app"
codesign --verify --deep --strict --verbose=2 "$app"

# 3. Notarize and staple the app, so it also passes when copied out of the disk image and opened offline.
if $notarize_on; then
  ditto -c -k --keepParent "$app" "$temp/app-notarize.zip"
  notarize "$temp/app-notarize.zip"
  xcrun stapler staple "$app"
  xcrun stapler validate "$app"
fi

# 4. The disk image: the app and a shortcut to Applications.
stage="$temp/dmg-stage"; rm -rf "$stage"; mkdir -p "$stage"
cp -R "$app" "$stage/"
ln -s /Applications "$stage/Applications"
dmg="$out/Gears3_Multipatch_Launcher_macOS.dmg"
rm -f "$dmg"
hdiutil create -volname "$name" -srcfolder "$stage" -ov -format UDZO "$dmg"
if $real; then
  codesign --force --timestamp --keychain "$keychain" --sign "$identity" "$dmg"
fi
if $notarize_on; then
  notarize "$dmg"
  xcrun stapler staple "$dmg"
  xcrun stapler validate "$dmg"
  spctl --assess --type open --context context:primary-signature -v "$dmg" || echo "::warning::spctl does not accept the disk image."
  spctl --assess --type execute -v "$app" || echo "::warning::spctl does not accept the app."
fi
echo "Built $dmg"

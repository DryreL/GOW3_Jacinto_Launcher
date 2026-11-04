#!/usr/bin/env bash
# Signs every Mach-O file in a publish folder with a Developer ID, with Apple's secure timestamp, and notarizes the result.
# CI runs it when the five secrets in DEVELOPMENT.md exist. A bare executable cannot hold a stapled ticket, so Gatekeeper asks Apple online the first time.
set -euo pipefail
folder="$1"
entitlements="$(cd "$(dirname "$0")/.." && pwd)/packaging/macos-entitlements.plist"
keychain="$RUNNER_TEMP/jacinto-signing.keychain-db"
certificate="$RUNNER_TEMP/jacinto-signing.p12"
upload="$RUNNER_TEMP/jacinto-notarize.zip"
password="$(uuidgen)"
trap 'security delete-keychain "$keychain" 2>/dev/null || true; rm -f "$certificate" "$upload"' EXIT

printf '%s' "$MACOS_CERTIFICATE_P12" | base64 --decode > "$certificate"
security create-keychain -p "$password" "$keychain"
security set-keychain-settings -lut 21600 "$keychain"
security unlock-keychain -p "$password" "$keychain"
security import "$certificate" -k "$keychain" -P "$MACOS_CERTIFICATE_PASSWORD" -T /usr/bin/codesign
security set-key-partition-list -S apple-tool:,apple: -s -k "$password" "$keychain" > /dev/null
security list-keychains -d user -s "$keychain" login.keychain-db
identity="$(security find-identity -v -p codesigning "$keychain" | awk '/Developer ID Application/ { print $2; exit }')"
[ -n "$identity" ] || { echo "The certificate holds no Developer ID Application identity."; exit 1; }

find "$folder" -type f -print0 | while IFS= read -r -d '' file; do
  if file -b "$file" | grep -q 'Mach-O'; then
    codesign --force --timestamp --options runtime --entitlements "$entitlements" --keychain "$keychain" --sign "$identity" "$file"
    codesign --verify --strict "$file"
    echo "Signed $file"
  fi
done

ditto -c -k --keepParent "$folder" "$upload"
result="$(xcrun notarytool submit "$upload" --apple-id "$APPLE_ID" --password "$APPLE_APP_PASSWORD" --team-id "$APPLE_TEAM_ID" --wait --output-format json)"
status="$(printf '%s' "$result" | python3 -c 'import json, sys; print(json.load(sys.stdin)["status"])')"
if [ "$status" != Accepted ]; then
  id="$(printf '%s' "$result" | python3 -c 'import json, sys; print(json.load(sys.stdin)["id"])')"
  xcrun notarytool log "$id" --apple-id "$APPLE_ID" --password "$APPLE_APP_PASSWORD" --team-id "$APPLE_TEAM_ID" || true
  echo "Notarization ended as: $status"
  exit 1
fi
echo "Notarized."

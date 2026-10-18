#!/bin/bash
# DMG with the app + symlink to /Applications (classic drag-to-install).
set -euo pipefail
cd "$(dirname "$0")/.."

APP="dist/FluentScanner.app"
[ -d "$APP" ] || { echo "missing $APP — run packaging/bundle.sh first"; exit 1; }

STAGE="dist/stage"
TMPDMG="dist/tmp.dmg"
rm -rf "$STAGE" "$TMPDMG" dist/FluentScanner.dmg
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"

echo "==> dmg (read-write staging)"
hdiutil create -volname "FluentScanner" -srcfolder "$STAGE" -ov -format UDRW "$TMPDMG" >/dev/null
MNT="$(hdiutil attach -readwrite -noverify -noautoopen "$TMPDMG" | cut -f 3 | grep -E '/Volumes/' | head -1)"
[ -n "${MNT:-}" ] || { echo "mount failed"; exit 1; }
cleanup() { hdiutil detach "$MNT" >/dev/null 2>&1 || true; }
trap cleanup EXIT
cleanup
trap - EXIT

echo "==> compress"
hdiutil convert "$TMPDMG" -format UDZO -o dist/FluentScanner.dmg >/dev/null
rm -rf "$STAGE" "$TMPDMG"
ls -la dist/FluentScanner.dmg

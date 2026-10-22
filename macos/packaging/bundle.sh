#!/bin/bash
# Builds FluentScanner.app (no Xcode needed: swift build + manual bundle).
set -euo pipefail
cd "$(dirname "$0")/.."

echo "==> swift build (release)"
swift build -c release 2>&1 | tail -2

BIN=".build/release/FluentScanner"
APP="dist/FluentScanner.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp "$BIN" "$APP/Contents/MacOS/FluentScanner"
cp packaging/Info.plist "$APP/Contents/Info.plist"
cp -R Sources/FluentScanner/Resources/en.lproj Sources/FluentScanner/Resources/fr.lproj Sources/FluentScanner/Resources/it.lproj Sources/FluentScanner/Resources/de.lproj Sources/FluentScanner/Resources/es.lproj \
  "$APP/Contents/Resources/"
cp Sources/FluentScanner/Resources/oui.csv "$APP/Contents/Resources/"

# Apple Help Book (chapters + menu search): HTML per language, indexed
# with hiutil (Spotlight format, anchors on, stopwords per language).
HELP="$APP/Contents/Resources/FluentScanner.help/Contents"
rm -rf "$APP/Contents/Resources/FluentScanner.help"
mkdir -p "$HELP/Resources"
cp packaging/help-Info.plist "$HELP/Info.plist"
for lang in en fr it de es; do
  mkdir -p "$HELP/Resources/$lang.lproj"
  cp packaging/help-src/$lang.lproj/*.html "$HELP/Resources/$lang.lproj/"
  hiutil -I lsm -C \
    -f "$HELP/Resources/$lang.lproj/FluentScanner.helpindex" \
    "$HELP/Resources/$lang.lproj/"
  hiutil -I corespotlight -C -a -s $lang -l $lang \
    -f "$HELP/Resources/$lang.lproj/FluentScanner.cshelpindex" \
    "$HELP/Resources/$lang.lproj/"
done
echo "==> help book"
ls "$HELP/Resources"

# Icon: hand-drawn radar sweep (packaging/AppIcon.svg).
# Rendered with rsvg-convert (brew install librsvg): Quick Look's qlmanage
# renders SVGs shrunken and off-center, so it must NOT be used here.
SRC_SVG="packaging/AppIcon.svg"
if [ -f "$SRC_SVG" ]; then
  command -v rsvg-convert >/dev/null || { echo "missing rsvg-convert (brew install librsvg)"; exit 1; }
  echo "==> icon"
  rm -rf /tmp/FS.iconset && mkdir -p /tmp/FS.iconset
  rsvg-convert -w 1024 -h 1024 "$SRC_SVG" -o /tmp/FS.iconset/base1024.png
  for s in 16 32 128 256 512; do
    sips -z $s $s /tmp/FS.iconset/base1024.png --out /tmp/FS.iconset/icon_${s}x${s}.png >/dev/null
    sips -z $((s*2)) $((s*2)) /tmp/FS.iconset/base1024.png --out /tmp/FS.iconset/icon_${s}x${s}@2x.png >/dev/null
  done
  rm /tmp/FS.iconset/base1024.png
  iconutil -c icns /tmp/FS.iconset -o "$APP/Contents/Resources/AppIcon.icns"
  rm -rf /tmp/FS.iconset
fi
codesign --force --deep --sign - "$APP" 2>/dev/null || true
echo "==> $APP"
ls -la dist/ | grep app

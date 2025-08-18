#!/usr/bin/env bash
set -euo pipefail
# Usage: ./scripts/package-macos-dmg.sh osx-x64|osx-arm64
RID=${1:-osx-arm64}
APP_NAME=Furchive
ROOT_DIR=$(cd "$(dirname "$0")/.." && pwd)
PUBLISH_DIR="$ROOT_DIR/src/Furchive.Avalonia/publish/$RID"
OUT_DIR="$ROOT_DIR/dist/$RID"
DMG_PATH="$OUT_DIR/${APP_NAME}-${RID}.dmg"

mkdir -p "$OUT_DIR"
if [ ! -d "$PUBLISH_DIR" ]; then
  dotnet publish "$ROOT_DIR/src/Furchive.Avalonia/Furchive.Avalonia.csproj" -c Release -r "$RID" --self-contained true -p:PublishTrimmed=false -o "$PUBLISH_DIR"
fi

APP_DIR="$OUT_DIR/${APP_NAME}.app/Contents/MacOS"
mkdir -p "$APP_DIR"
cp -R "$PUBLISH_DIR"/* "$APP_DIR"/

# Minimal Info.plist
PLIST="$OUT_DIR/${APP_NAME}.app/Contents/Info.plist"
cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>${APP_NAME}</string>
  <key>CFBundleExecutable</key><string>Furchive</string>
  <key>CFBundleIdentifier</key><string>com.furchive.app</string>
  <key>CFBundleVersion</key><string>1.0</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
</dict>
</plist>
EOF

# Create DMG (requires hdiutil on macOS)
hdiutil create -volname "${APP_NAME}" -srcfolder "$OUT_DIR/${APP_NAME}.app" -ov -format UDZO "$DMG_PATH"
echo "DMG created at $DMG_PATH" 

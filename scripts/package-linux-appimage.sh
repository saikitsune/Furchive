#!/usr/bin/env bash
set -euo pipefail
# Usage: ./scripts/package-linux-appimage.sh linux-x64
RID=${1:-linux-x64}
APP_NAME=Furchive
ROOT_DIR=$(cd "$(dirname "$0")/.." && pwd)
PUBLISH_DIR="$ROOT_DIR/src/Furchive.Avalonia/publish/$RID"
OUT_DIR="$ROOT_DIR/dist/$RID"
APPDIR="$OUT_DIR/${APP_NAME}.AppDir"

mkdir -p "$OUT_DIR"
if [ ! -d "$PUBLISH_DIR" ]; then
  dotnet publish "$ROOT_DIR/src/Furchive.Avalonia/Furchive.Avalonia.csproj" -c Release -r "$RID" --self-contained true -p:PublishTrimmed=false -o "$PUBLISH_DIR"
fi

# Prepare AppDir structure
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps"
cp -R "$PUBLISH_DIR"/* "$APPDIR/usr/bin/"
cp "$ROOT_DIR/assets/icon256.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/${APP_NAME}.png"

# Desktop entry
cat > "$APPDIR/${APP_NAME}.desktop" <<EOF
[Desktop Entry]
Name=${APP_NAME}
Exec=/usr/bin/Furchive
Icon=${APP_NAME}
Type=Application
Categories=Graphics;
Terminal=false
EOF

# AppRun launcher
cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE=$(dirname "$(readlink -f "$0")")
export PATH="$HERE/usr/bin:$PATH"
exec "$HERE/usr/bin/Furchive" "$@"
EOF
chmod +x "$APPDIR/AppRun"

# Create AppImage (requires appimagetool)
APPIMAGE="$OUT_DIR/${APP_NAME}-${RID}.AppImage"
appimagetool "$APPDIR" "$APPIMAGE"
echo "AppImage created at $APPIMAGE"

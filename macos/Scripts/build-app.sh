#!/bin/bash
# Builds Mason Agent for macOS from this package:
#   dist/Mason Agent.app            the app, with the mason command line tool inside it
#   dist/MasonAgent-<version>-macos.zip   the app alone, for dragging into Applications
#   dist/MasonAgent-<version>-macos.pkg   an installer that also links mason into /usr/local/bin
# Run on a Mac with Xcode's command line tools. The build is signed ad hoc, not notarized.
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION="${VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' ../src/MasonAgent/MasonAgent.csproj | head -1)}"
[ -n "$VERSION" ] || { echo "Set VERSION."; exit 1; }
ARCHS="${ARCHS:---arch arm64 --arch x86_64}"

swift build -c release $ARCHS
BIN="$(swift build -c release $ARCHS --show-bin-path)"

APP="dist/Mason Agent.app"
rm -rf dist && mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources/bin"
cp "$BIN/MasonAgent" "$APP/Contents/MacOS/MasonAgent"
cp "$BIN/mason" "$APP/Contents/Resources/bin/mason"
sed "s/__VERSION__/$VERSION/g" Packaging/Info.plist > "$APP/Contents/Info.plist"
cp ../LICENSE ../THIRD-PARTY-NOTICES.md "$APP/Contents/Resources/"

# App icon from the brand kit's largest square mark.
ICON_SOURCE="../brand/logo-kit/favicon/android-chrome-512x512.png"
ICONSET="$(mktemp -d)/AppIcon.iconset" && mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  sips -z $size $size "$ICON_SOURCE" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z $double $double "$ICON_SOURCE" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

# Sign inside out. "-" is an ad hoc signature: it lets the app run on Apple silicon but is not a Developer ID.
codesign --force --sign - "$APP/Contents/Resources/bin/mason"
codesign --force --sign - "$APP"
codesign --verify --deep --strict "$APP"
"$APP/Contents/Resources/bin/mason" --help >/dev/null

ditto -c -k --keepParent "$APP" "dist/MasonAgent-$VERSION-macos.zip"

ROOT="$(mktemp -d)" && mkdir -p "$ROOT/Applications" "$ROOT/scripts"
ditto "$APP" "$ROOT/Applications/Mason Agent.app"
install -m 755 Packaging/postinstall "$ROOT/scripts/postinstall"
# Without this, the installer treats the app as relocatable: it updates any copy of Mason Agent it finds elsewhere
# on the disk instead of installing into /Applications, and the mason link would then point at nothing.
pkgbuild --analyze --root "$ROOT/Applications" "$ROOT/component.plist" >/dev/null
/usr/libexec/PlistBuddy -c "Set :0:BundleIsRelocatable false" "$ROOT/component.plist"
pkgbuild --root "$ROOT/Applications" --component-plist "$ROOT/component.plist" --install-location /Applications --scripts "$ROOT/scripts" \
  --identifier site.masonagent.MasonAgent --version "$VERSION" "dist/MasonAgent-$VERSION-macos.pkg"
( cd dist && shasum -a 256 "MasonAgent-$VERSION-macos.zip" "MasonAgent-$VERSION-macos.pkg" > SHA256SUMS.txt )
ls -la dist

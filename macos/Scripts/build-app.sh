#!/bin/bash
# Builds Mason Agent for macOS from this package:
#   dist/Mason Agent.app            the app, with the mason command line tool inside it
#   dist/MasonAgent-<version>-macos.zip   the app alone, for dragging into Applications
#   dist/MasonAgent-<version>-macos.pkg   an installer that also links mason into /usr/local/bin
# Run on a Mac with Xcode's command line tools. The build is signed ad hoc, not notarized.
set -euo pipefail
cd "$(dirname "$0")/.."
# The Mac's version lives in its own source, Sources/MasonCore/Version.swift.
OWN="$(sed -n 's/.*static let current = "\(.*\)".*/\1/p' Sources/MasonCore/Version.swift | head -1)"
[ -n "$OWN" ] || { echo "Sources/MasonCore/Version.swift has no version."; exit 1; }
VERSION="${VERSION:-$OWN}"
[ "$VERSION" = "$OWN" ] || { echo "Sources/MasonCore/Version.swift says $OWN but the build asks for $VERSION. Run scripts/version.sh set $VERSION first."; exit 1; }
ARCHS="${ARCHS:---arch arm64 --arch x86_64}"

swift build -c release $ARCHS
BIN="$(swift build -c release $ARCHS --show-bin-path)"

APP="dist/Mason Agent.app"
rm -rf dist && mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources/bin"
cp "$BIN/MasonAgent" "$APP/Contents/MacOS/MasonAgent"
cp "$BIN/mason" "$APP/Contents/Resources/bin/mason"
sed "s/__VERSION__/$VERSION/g" Packaging/Info.plist > "$APP/Contents/Info.plist"
cp ../LICENSE ../THIRD-PARTY-NOTICES.md "$APP/Contents/Resources/"
# The mascot, for the brand mark, Mason's answers and the welcome page.
cp Packaging/mascot.png Packaging/welcome.png "$APP/Contents/Resources/"
# The Python program that serves Transformers weights, and the list of architectures it accepts.
mkdir -p "$APP/Contents/Resources/transformers"
cp ../windows/scripts/Serve-Transformers.py ../windows/scripts/ModelAdapters.json "$APP/Contents/Resources/transformers/"

# Jedi and the TypeScript compiler for inspect_symbol, the same pinned versions the Windows app ships.
mkdir -p "$APP/Contents/Resources/language-tools"
cp ../windows/scripts/language-tools/python-symbols.py ../windows/scripts/language-tools/typescript-symbols.cjs "$APP/Contents/Resources/language-tools/"
../linux/packaging/prepare-language-tools.sh "$APP/Contents/Resources/language-tools"

# The app icon (16 to 1024 px, light and dark Finder backgrounds alike).
cp Packaging/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"

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

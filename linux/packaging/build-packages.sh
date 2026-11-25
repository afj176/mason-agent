#!/bin/bash
# Builds Mason Agent for Linux and packages it:
#   dist/mason-agent_<version>_<arch>.deb          for Debian and Ubuntu; installs /usr/bin/mason and /usr/bin/mason-agent
#   dist/mason-agent-<version>-linux-<arch>.tar.gz for other distributions; its install.sh installs under ~/.local
# Run on Ubuntu 24.04 or in the container from Dockerfile.build:
#   docker run --rm -v "$PWD:/src" mason-linux-build /src/linux/packaging/build-packages.sh
set -euo pipefail
cd "$(dirname "$0")/.."
SOURCE="$PWD"
VERSION="$(sed -n "s/^  version: '\(.*\)',$/\1/p" meson.build | head -1)"
[ -n "$VERSION" ] || { echo "No version in meson.build."; exit 1; }
ARCH="$(dpkg --print-architecture)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
DIST="${DIST:-$SOURCE/dist}"
rm -rf "$DIST" && mkdir -p "$DIST"

meson setup "$WORK/build" "$SOURCE" --prefix=/usr --buildtype=release -Db_lto=true >/dev/null
meson compile -C "$WORK/build"
meson test -C "$WORK/build" --print-errorlogs
DESTDIR="$WORK/root" meson install -C "$WORK/build" >/dev/null
strip "$WORK/root/usr/bin/mason" "$WORK/root/usr/bin/mason-agent"
install -Dm644 "$SOURCE/../LICENSE" "$WORK/root/usr/share/doc/mason-agent/copyright"

# ---- .deb ----
# Library dependencies are read from the binaries, so the package asks for exactly what they link against.
mkdir -p "$WORK/shlibs/debian" && printf 'Source: mason-agent\n\nPackage: mason-agent\nArchitecture: any\n' > "$WORK/shlibs/debian/control"
DEPENDS="$(cd "$WORK/shlibs" && dpkg-shlibdeps -O "$WORK/root/usr/bin/mason" "$WORK/root/usr/bin/mason-agent" 2>/dev/null | sed 's/^shlibs:Depends=//')"
mkdir -p "$WORK/root/DEBIAN"
cat > "$WORK/root/DEBIAN/control" <<EOF
Package: mason-agent
Version: $VERSION
Section: devel
Priority: optional
Architecture: $ARCH
Depends: $DEPENDS, adwaita-icon-theme, librsvg2-common
Recommends: bubblewrap
Installed-Size: $(du -sk "$WORK/root/usr" | cut -f1)
Maintainer: Mason Agent <noreply@masonagent.site>
Homepage: https://masonagent.site
Description: Coding workspace that runs on your own local models
 Mason Agent is a desktop app (mason-agent) and a command line (mason) for
 working on a project with a local model: reviewed edits, commands with
 approvals, diffs and undo. In Auto, commands run in a bubblewrap sandbox
 limited to the project.
EOF
dpkg-deb --root-owner-group --build "$WORK/root" "$DIST/mason-agent_${VERSION}_${ARCH}.deb" >/dev/null

# ---- portable archive ----
PORTABLE="$WORK/mason-agent-$VERSION-linux-$ARCH"
mkdir -p "$PORTABLE"
cp -r "$WORK/root/usr/bin" "$WORK/root/usr/share" "$PORTABLE/"
rm -rf "$PORTABLE/share/doc"
cp "$SOURCE/../LICENSE" "$PORTABLE/"
install -m755 "$SOURCE/packaging/install.sh" "$PORTABLE/install.sh"
tar -C "$WORK" -czf "$DIST/mason-agent-$VERSION-linux-$ARCH.tar.gz" "mason-agent-$VERSION-linux-$ARCH"

( cd "$DIST" && sha256sum * > SHA256SUMS.txt )
ls -la "$DIST"

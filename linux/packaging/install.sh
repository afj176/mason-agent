#!/bin/sh
# Installs Mason Agent for the current user, without administrator rights:
#   ~/.local/bin/mason and ~/.local/bin/mason-agent, plus the menu entry and icon under ~/.local/share.
# Run it from the extracted archive:  ./install.sh        Remove everything again with:  ./install.sh --uninstall
# The programs need GTK 4, gtkmm 4.10+, libadwaita 1.4+ and libcurl from your distribution; bubblewrap enables Auto.
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
prefix="${PREFIX:-$HOME/.local}"
desktop="$prefix/share/applications/site.masonagent.MasonAgent.desktop"

if [ "${1:-}" = "--uninstall" ]; then
    rm -f "$prefix/bin/mason" "$prefix/bin/mason-agent" "$desktop" \
          "$prefix/share/metainfo/site.masonagent.MasonAgent.metainfo.xml" \
          "$prefix/share/icons/hicolor/512x512/apps/site.masonagent.MasonAgent.png"
    rm -rf "$prefix/share/mason-agent"
    echo "Mason Agent was removed from $prefix. Your sessions and models in ~/.local/share/MasonAgent were left alone."
    exit 0
fi

mkdir -p "$prefix/bin" "$prefix/share/applications" "$prefix/share/metainfo" "$prefix/share/icons/hicolor/512x512/apps"
install -m755 "$here/bin/mason" "$here/bin/mason-agent" "$prefix/bin/"
install -m644 "$here/share/metainfo/site.masonagent.MasonAgent.metainfo.xml" "$prefix/share/metainfo/"
install -m644 "$here/share/icons/hicolor/512x512/apps/site.masonagent.MasonAgent.png" "$prefix/share/icons/hicolor/512x512/apps/"
# The Python program that serves Transformers weights; mason looks for it beside its own bin folder.
mkdir -p "$prefix/share/mason-agent/transformers"
install -m644 "$here/share/mason-agent/transformers/Serve-Transformers.py" "$here/share/mason-agent/transformers/ModelAdapters.json" "$prefix/share/mason-agent/transformers/"
# The menu entry starts the app by its full path, so it works even when ~/.local/bin is not on PATH for the desktop session.
sed "s|^Exec=mason-agent|Exec=$prefix/bin/mason-agent|" "$here/share/applications/site.masonagent.MasonAgent.desktop" > "$desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$prefix/share/applications" >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q "$prefix/share/icons/hicolor" >/dev/null 2>&1 || true

echo "Mason Agent is installed in $prefix."
case ":$PATH:" in
    *":$prefix/bin:"*) echo "Open a terminal in a project folder and type: mason" ;;
    *) echo "$prefix/bin is not on your PATH yet. Add this line to ~/.profile (or your shell's startup file), then open a new terminal:"
       echo "    export PATH=\"$prefix/bin:\$PATH\"" ;;
esac
missing=""
for library in libgtkmm-4.0.so.0 libadwaita-1.so.0 libcurl.so.4; do
    ldconfig -p 2>/dev/null | grep -q "$library" || missing="$missing $library"
done
[ -z "$missing" ] || echo "These libraries were not found; install them with your package manager before starting Mason Agent:$missing"
command -v bwrap >/dev/null 2>&1 || echo "bubblewrap (bwrap) is not installed. Mason works without it, but Auto will refuse to run commands."

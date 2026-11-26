#!/bin/bash
# Mason Agent's version, which each platform keeps in its own folder:
#   windows/src/MasonAgent/MasonAgent.csproj   <Version>
#   macos/Sources/MasonCore/Version.swift      MasonVersion.current
#   linux/meson.build                          project version
# All three are released together under one tag, so they always hold the same number.
#
#   scripts/version.sh                  print the version (fails if the platforms disagree)
#   scripts/version.sh set 2.3.0        set all three
#   scripts/version.sh bump patch       2.2.1 → 2.2.2 (minor → 2.3.0, major → 3.0.0); prints the new version
set -euo pipefail
cd "$(dirname "$0")/.."

WINDOWS=windows/src/MasonAgent/MasonAgent.csproj
MACOS=macos/Sources/MasonCore/Version.swift
LINUX=linux/meson.build

read_windows() { sed -n 's:.*<Version>\([0-9.]*\)</Version>.*:\1:p' "$WINDOWS" | head -1; }
read_macos() { sed -n 's/.*static let current = "\([0-9.]*\)".*/\1/p' "$MACOS" | head -1; }
read_linux() { sed -n "s/^  version: '\([0-9.]*\)',.*/\1/p" "$LINUX" | head -1; }

current() {
  local w m l
  w="$(read_windows)"; m="$(read_macos)"; l="$(read_linux)"
  if [ -z "$w" ] || [ "$w" != "$m" ] || [ "$w" != "$l" ]; then
    echo "The platforms disagree: Windows '$w', macOS '$m', Linux '$l'. Run scripts/version.sh set X.Y.Z." >&2
    exit 1
  fi
  echo "$w"
}

valid() { [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "A version looks like 1.2.3, not '$1'." >&2; exit 1; }; }

set_all() {
  valid "$1"
  sed -i.bak "s:<Version>[0-9.]*</Version>:<Version>$1</Version>:" "$WINDOWS"
  sed -i.bak "s/static let current = \"[0-9.]*\"/static let current = \"$1\"/" "$MACOS"
  sed -i.bak "s/^  version: '[0-9.]*',/  version: '$1',/" "$LINUX"
  rm -f "$WINDOWS.bak" "$MACOS.bak" "$LINUX.bak"
  [ "$(current)" = "$1" ] || { echo "Setting $1 did not take in every file." >&2; exit 1; }
}

case "${1:-}" in
  "") current ;;
  set) set_all "${2:?Give the version, for example scripts/version.sh set 2.3.0}"; current ;;
  bump)
    IFS=. read -r major minor patch <<< "$(current)"
    case "${2:-patch}" in
      major) next="$((major + 1)).0.0" ;;
      minor) next="$major.$((minor + 1)).0" ;;
      patch) next="$major.$minor.$((patch + 1))" ;;
      *) echo "Bump major, minor or patch, not '$2'." >&2; exit 1 ;;
    esac
    set_all "$next"; echo "$next" ;;
  *) echo "Use scripts/version.sh, scripts/version.sh set X.Y.Z or scripts/version.sh bump patch|minor|major." >&2; exit 1 ;;
esac

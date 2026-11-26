#!/bin/sh
# Fetches the pinned language tools that inspect_symbol uses, the same versions the Windows app ships:
#   Jedi and Parso (from windows/scripts/language-tools/requirements.txt, checked against their hashes), and
#   the TypeScript compiler (from package-lock.json, checked against its integrity value).
# Usage: prepare-language-tools.sh DESTINATION
# The macOS and Linux package builds run this first. It needs python3 with pip, and npm. Nothing here is run at
# build time: the files are only copied into DESTINATION/python and DESTINATION/typescript.
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
config="$here/../../windows/scripts/language-tools"
target="${1:?Give the folder to fill.}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

rm -rf "$target/python" "$target/typescript"
mkdir -p "$target/python" "$target/typescript"

python3 -m pip download --quiet --only-binary=:all: --require-hashes --no-deps --dest "$work/wheels" -r "$config/requirements.txt"
python3 -m pip install --quiet --no-index --find-links "$work/wheels" --no-deps --require-hashes --no-compile --target "$target/python" -r "$config/requirements.txt"

cp "$config/package.json" "$config/package-lock.json" "$work/"
npm ci --prefix "$work" --ignore-scripts --no-audit --no-fund >/dev/null
cp "$work/node_modules/typescript/lib/typescript.js" "$work/node_modules/typescript/LICENSE.txt" "$target/typescript/"
cp "$work"/node_modules/typescript/lib/lib.*.d.ts "$target/typescript/"

[ -f "$target/python/jedi/__init__.py" ] && [ -f "$target/typescript/typescript.js" ] || { echo "The language tools are incomplete."; exit 1; }
echo "Language tools are in $target: Jedi and TypeScript $(sed -n 's/.*"version": *"\([0-9.]*\)".*/\1/p' "$work/node_modules/typescript/package.json" | head -1)."

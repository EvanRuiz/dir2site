#!/bin/zsh
# package-vscode-extension.sh — rebuilds the VS Code extension the app installs
# Run from the repo root: scripts/package-vscode-extension.sh
#
# The .vsix is committed rather than built during the normal build, so packaging needs no node
# toolchain in CI. The cost is that it goes stale the moment the extension source changes, which
# BundledVsCodeExtensionTests catches — run this when it does.
#
# Versioning: the extension carries the app release it ships in, so a branch that changes it sets
# package.json and VsCodeExtensionInstaller.Version to the *next* release tag once, then repackages
# as often as it likes at that number. Re-running this script is not a reason to bump — an unshipped
# version names whatever the branch finally merges. Bumping per rebuild burns versions that were
# never released and reads as a gap in the history.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${(%):-%x}")/.." && pwd)"
SOURCE_DIR="$REPO_ROOT/editors/vscode-dir2site-markdown"
OUTPUT="$REPO_ROOT/Assets/editors/dir2site-markdown.vsix"

if ! command -v npx >/dev/null 2>&1; then
  echo "npx not found — install Node.js to repackage the extension." >&2
  exit 1
fi

mkdir -p "$(dirname "$OUTPUT")"

# vsce reads version, publisher and name from the extension's own package.json; the tests assert
# the packaged copy still agrees with the sources beside it.
( cd "$SOURCE_DIR" && npx --yes @vscode/vsce package --out "$OUTPUT" )

echo
echo "Packaged: ${OUTPUT#"$REPO_ROOT"/}"
echo "Commit it — the app ships this file, and dotnet test compares it against the source."

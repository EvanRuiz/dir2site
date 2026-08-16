#!/bin/zsh
# generate-notices.sh — regenerates THIRD_PARTY_NOTICES.md
# Run from the repo root: scripts/generate-notices.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${(%):-%x}")/.." && pwd)"
OUTPUT_ROOT="$REPO_ROOT/THIRD_PARTY_NOTICES.md"
OUTPUT_APP="$REPO_ROOT/Assets/app/about/THIRD_PARTY_NOTICES.md"

mkdir -p "$REPO_ROOT/Assets/app/about"

# ---------------------------------------------------------------------------
# 1. NuGet packages — dotnet-project-licenses
# ---------------------------------------------------------------------------
# The tool ships as net7.0 and no .NET 7 runtime is installed any more, so it needs the same
# roll-forward the release scripts use for vpk. Without it the run failed, the failure was hidden,
# and the notices were quietly built from a table of name-prefix guesses instead — which had
# drifted (it recorded NetVips.Native as LGPL-2.1 where the package says LGPL-3.0-or-later). A
# notices file that is silently wrong is worse than one that fails to build, so there is no
# fallback now: if the tool cannot run, the script stops.

# Add ~/.dotnet/tools to PATH for this session
export PATH="$PATH:$HOME/.dotnet/tools"
export DOTNET_ROLL_FORWARD=LatestMajor

TMP_DIR="/tmp/notices-$$"
mkdir -p "$TMP_DIR"

NUGET_ROWS=""

if ! command -v dotnet-project-licenses &>/dev/null; then
  echo "Installing dotnet-project-licenses..." >&2
  dotnet tool install --global dotnet-project-licenses 2>/dev/null || true
fi

if command -v dotnet-project-licenses &>/dev/null; then
  JSON_FILE="$TMP_DIR/licenses.json"
  # --include-transitive because what the licenses cover is what we distribute, and a package
  # pulled in by another ships in the installer just the same. Without it the file lists 22 of the
  # 174 packages in the build — including, until this was put back, Svg.Custom, the one MS-PL
  # licence in the tree.
  if dotnet-project-licenses \
      --input "$REPO_ROOT/dir2site.csproj" \
      --json \
      --outfile "$JSON_FILE" \
      --include-transitive >/dev/null; then
    if [[ -s "$JSON_FILE" ]]; then
      NUGET_ROWS="$(python3 - "$JSON_FILE" <<'PYEOF'
import json, sys

with open(sys.argv[1]) as f:
    data = json.load(f)

packages = []
if isinstance(data, list):
    items = data
elif isinstance(data, dict):
    items = data.get("packages", data.get("Packages", []))
else:
    items = []

for p in items:
    name    = p.get("PackageName") or p.get("packageName") or p.get("name") or ""
    version = p.get("PackageVersion") or p.get("packageVersion") or p.get("version") or ""
    lic     = p.get("License") or p.get("license") or p.get("LicenseType") or ""
    url     = p.get("PackageUrl") or p.get("packageUrl") or p.get("licenseUrl") or p.get("LicenseUrl") or ""
    # Packages that declare a licenseUrl but no SPDX id, each read from the project's own LICENSE
    # file. Anything not listed here and not declared stops the run below.
    lic = lic or {
        "EmbedIO": "MIT",
        "Unosquare.Swan.Lite": "MIT",  # EmbedIO's own dependency, same house, same licence
    }.get(name, "")
    if name:
        packages.append((name.strip(), version.strip(), lic.strip(), url.strip()))

packages.sort(key=lambda x: x[0].lower())

undeclared = [f"{n} {v}" for n, v, lic, _ in packages if not lic]
if undeclared:
    print("error: these packages declare no license:", file=sys.stderr)
    for item in undeclared:
        print(f"  - {item}", file=sys.stderr)
    sys.exit(1)

for name, version, lic, url in packages:
    print(f"{name}\t{version}\t{lic}\t{url}")
PYEOF
)"
    fi
  fi
fi

# No fallback: guessing licenses is how this file went wrong in the first place.
if [[ -z "$NUGET_ROWS" ]]; then
  echo "error: could not read package licenses via dotnet-project-licenses." >&2
  echo "Nothing was written." >&2
  exit 1
fi

rm -rf "$TMP_DIR"

# ---------------------------------------------------------------------------
# 2. Vendored libraries
# ---------------------------------------------------------------------------

scan_vendor_dirs() {
  python3 - "$REPO_ROOT" <<'PYEOF'
import os, sys, re

REPO_ROOT = sys.argv[1]

VENDOR_META = {
    "openseadragon-bin": ("OpenSeadragon",             "BSD-3-Clause", "https://github.com/openseadragon/openseadragon"),
    "bookreader":        ("BookReader (Internet Archive)", "AGPL-3.0", "https://github.com/internetarchive/bookreader"),
    "bootstrap-icons":   ("Bootstrap Icons",            "MIT",         "https://github.com/twbs/icons"),
    "bootstrap":         ("Bootstrap",                  "MIT",         "https://github.com/twbs/bootstrap"),
}

def match_meta(folder):
    # Longest prefix match
    best = ("", None)
    for key, val in VENDOR_META.items():
        if folder.startswith(key) and len(key) > len(best[0]):
            best = (key, val)
    return best[1]

def extract_version(folder):
    m = re.search(r'(\d+\.\d+(?:\.\d+)*)', folder)
    return m.group(1) if m else ""

rows = []

for search_dir, rel_prefix in [
    (os.path.join(REPO_ROOT, "Assets", "js"),    "Assets/js"),
    (os.path.join(REPO_ROOT, "Assets", "icons"), "Assets/icons"),
]:
    if not os.path.isdir(search_dir):
        continue
    for folder in sorted(os.listdir(search_dir)):
        full_path = os.path.join(search_dir, folder)
        if not os.path.isdir(full_path):
            continue
        # A Finder duplicate ("bootstrap-icons-1.13.1 2") is not a second dependency, and once it
        # lands in this table it reads as one we actually ship.
        if re.search(r' \d+$', folder):
            continue
        meta = match_meta(folder)
        if not meta:
            continue
        display_name, lic_type, src_url = meta
        version = extract_version(folder)
        location = f"{rel_prefix}/{folder}"

        rows.append((display_name, version, lic_type, location, src_url))

rows.sort(key=lambda x: x[0].lower())

for r in rows:
    print("\t".join(r))
PYEOF
}

VENDOR_ROWS="$(scan_vendor_dirs)"

# The version lives in the test that pins rclone's hashes, so the two cannot disagree.
RCLONE_SOURCE="$REPO_ROOT/tests/dir2site.Tests/RcloneTool.cs"
RCLONE_VERSION="$(sed -n 's/.*public const string Version = "v\{0,1\}\([^"]*\)".*/\1/p' "$RCLONE_SOURCE" | head -1)"
if [[ -z "$RCLONE_VERSION" ]]; then
  echo "error: could not read the rclone version from $RCLONE_SOURCE." >&2
  exit 1
fi

# ---------------------------------------------------------------------------
# 3. Assemble Markdown
# ---------------------------------------------------------------------------

generate_md() {
  local include_header="${1:-false}"
  if [[ "$include_header" == "true" ]]; then
    cat <<'HEADER'
# Third-Party Notices

This file lists the open-source components used by dir2site and their licenses.
To regenerate this file after dependency changes, run: `scripts/generate-notices.sh`

---
HEADER
  fi

  cat <<'HEADER'

## NuGet Packages

| Package | Version | License | URL |
|---------|---------|---------|-----|
HEADER

  while IFS=$'\t' read -r name ver lic url; do
    [[ -z "$name" ]] && continue
    if [[ -n "$url" && "$url" != "(runtime packages)" ]]; then
      echo "| ${name} | ${ver} | ${lic} | [${url}](${url}) |"
    else
      echo "| ${name} | ${ver} | ${lic} | ${url} |"
    fi
  done <<< "$NUGET_ROWS"

  cat <<'VENDOR_HEADER'

---

## Included Third-Party Libraries

| Library | Version | License | Location | Source |
|---------|---------|---------|----------|--------|
VENDOR_HEADER

  while IFS=$'\t' read -r name ver lic loc src; do
    [[ -z "$name" ]] && continue
    echo "| ${name} | ${ver} | ${lic} | \`${loc}\` | [${src}](${src}) |"
  done <<< "$VENDOR_ROWS"

  # rclone is downloaded by the integration tests rather than vendored, so it has no folder to
  # scan. This section was hand-written into the generated file, where every run deleted it again.
  cat <<TOOLS_HEADER

---

## Test-Only Tools

Not shipped with dir2site and not distributed in this repository. Fetched on demand by the test
suite and verified against hashes pinned in \`tests/dir2site.Tests/RcloneTool.cs\`.

| Tool | Version | License | Source |
|------|---------|---------|--------|
| rclone | ${RCLONE_VERSION} | MIT | [https://github.com/rclone/rclone](https://github.com/rclone/rclone) |
TOOLS_HEADER
}

printf '%s\n' "$(generate_md true)"  > "$OUTPUT_ROOT"
printf '%s\n' "$(generate_md false)" > "$OUTPUT_APP"

echo "Written: $OUTPUT_ROOT"
echo "Written: $OUTPUT_APP"

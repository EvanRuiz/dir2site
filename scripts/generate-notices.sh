#!/bin/zsh
# generate-notices.sh — regenerates THIRD_PARTY_NOTICES.md
# Run from the repo root: scripts/generate-notices.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${(%):-%x}")/.." && pwd)"
OUTPUT_ROOT="$REPO_ROOT/THIRD_PARTY_NOTICES.md"
OUTPUT_APP="$REPO_ROOT/Assets/app/about/THIRD_PARTY_NOTICES.md"

mkdir -p "$REPO_ROOT/Assets/app/about"

# ---------------------------------------------------------------------------
# 1. NuGet packages — try dotnet-project-licenses, fall back to dotnet list
# ---------------------------------------------------------------------------

# Add ~/.dotnet/tools to PATH for this session
export PATH="$PATH:$HOME/.dotnet/tools"

TMP_DIR="/tmp/notices-$$"
mkdir -p "$TMP_DIR"

NUGET_ROWS=""

if ! command -v dotnet-project-licenses &>/dev/null; then
  echo "Installing dotnet-project-licenses..." >&2
  dotnet tool install --global dotnet-project-licenses 2>/dev/null || true
fi

if command -v dotnet-project-licenses &>/dev/null; then
  if dotnet-project-licenses \
      --input "$REPO_ROOT" \
      --output-directory "$TMP_DIR" \
      --output-file-type json \
      --include-transitive 2>/dev/null; then
    JSON_FILE="$(find "$TMP_DIR" -name '*.json' | head -1)"
    if [[ -n "$JSON_FILE" && -s "$JSON_FILE" ]]; then
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
    if name:
        packages.append((name.strip(), version.strip(), lic.strip(), url.strip()))

packages.sort(key=lambda x: x[0].lower())
for name, version, lic, url in packages:
    print(f"{name}\t{version}\t{lic}\t{url}")
PYEOF
)" 2>/dev/null || NUGET_ROWS=""
    fi
  fi
fi

# Fallback: parse `dotnet list package` with hardcoded license map
if [[ -z "$NUGET_ROWS" ]]; then
  echo "Falling back to dotnet list package + hardcoded license map..." >&2
  DOTNET_LIST_TMP="$TMP_DIR/dotnet-list.txt"
  dotnet list "$REPO_ROOT/dir2site.csproj" package --include-transitive 2>/dev/null \
    | grep '^\s*>' > "$DOTNET_LIST_TMP" || true
  NUGET_ROWS="$(python3 /dev/stdin "$DOTNET_LIST_TMP" <<'PYEOF'
import sys, re

LICENSE_MAP = [
    # (prefix, license, url) — longest prefix wins
    ("NetVips.Native",          "LGPL-2.1",    "https://github.com/libvips/libvips"),
    ("NetVips",                 "MIT",          "https://github.com/kleisauke/net-vips"),
    ("Magick.NET",              "Apache-2.0",   "https://github.com/dlemstra/Magick.NET"),
    ("SkiaSharp",               "MIT",          "https://github.com/mono/SkiaSharp"),
    ("HarfBuzzSharp",           "MIT",          "https://github.com/mono/SkiaSharp"),
    ("Avalonia",                "MIT",          "https://github.com/AvaloniaUI/Avalonia"),
    ("CommunityToolkit.Mvvm",   "MIT",          "https://github.com/CommunityToolkit/dotnet"),
    ("PDFtoImage",              "MIT",          "https://github.com/sungaila/PDFtoImage"),
    ("PdfPig",                  "Apache-2.0",   "https://github.com/UglyToad/PdfPig"),
    ("Scriban",                 "BSD-2-Clause", "https://github.com/scriban/scriban"),
    ("YamlDotNet",              "MIT",          "https://github.com/aaubry/YamlDotNet"),
    ("Mapster",                 "MIT",          "https://github.com/MapsterMapper/Mapster"),
    ("EmbedIO",                 "MIT",          "https://github.com/unosquare/embedio"),
    ("MessageBox.Avalonia",     "MIT",          "https://github.com/AvaloniaCommunity/MessageBox.Avalonia"),
    ("bblanchon.PDFium",        "Apache-2.0",   "https://github.com/bblanchon/pdfium-binaries"),
    ("runtime.",                "MIT",          "(runtime packages)"),
]

def lookup(pkg):
    best = ("", "Unknown", "")
    for prefix, lic, url in LICENSE_MAP:
        if pkg.startswith(prefix) and len(prefix) > len(best[0]):
            best = (prefix, lic, url)
    return best[1], best[2]

seen = {}
packages = []
with open(sys.argv[1]) as fh:
    for line in fh:
        m = re.match(r'\s*>\s+(\S+)\s+\S+\s+(\S+)', line)
        if m:
            pkg, ver = m.group(1), m.group(2)
            key = f"{pkg}@{ver}"
            if key not in seen:
                seen[key] = True
                lic, url = lookup(pkg)
                packages.append((pkg, ver, lic, url))

packages.sort(key=lambda x: x[0].lower())
for pkg, ver, lic, url in packages:
    print(f"{pkg}\t{ver}\t{lic}\t{url}")
PYEOF
)"
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
}

printf '%s\n' "$(generate_md true)"  > "$OUTPUT_ROOT"
printf '%s\n' "$(generate_md false)" > "$OUTPUT_APP"

echo "Written: $OUTPUT_ROOT"
echo "Written: $OUTPUT_APP"

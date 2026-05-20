#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$SCRIPT_DIR/.."
VERSION="${1:-0.0.1-test}"
RID="linux-x64"
PROFILE="linux-x64"
PUBLISH_DIR="bin/x64/Release/net10.0/linux-x64/publish"

echo "==> Platform: $RID  Version: $VERSION"

cd "$ROOT"

echo "==> Restoring tools..."
dotnet tool restore

echo "==> Publishing..."
RELEASE_VERSION="$VERSION" dotnet publish "/p:PublishProfile=$PROFILE" -c Release

echo "==> Packing with Velopack..."
OUTPUT_DIR="$ROOT/releases/$RID"
rm -rf "$OUTPUT_DIR"
DOTNET_ROLL_FORWARD=LatestMajor dotnet vpk pack \
  --packId dir2site \
  --packVersion "$VERSION" \
  --packDir "$ROOT/$PUBLISH_DIR" \
  --outputDir "$OUTPUT_DIR" \
  --runtime "$RID"

echo ""
echo "==> Done. Artifacts in releases/$RID:"
ls -lh "$OUTPUT_DIR"

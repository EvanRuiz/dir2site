#!/bin/bash
set -e

INPUT_SVG="$(dirname "$0")/../Assets/app/dir2site-icon.svg"
OUT="$(dirname "$0")/../Assets/app"

echo "Rendering PNGs..."
for SIZE in 16 32 48 64 128 256 512 1024; do
    rsvg-convert -w "$SIZE" -h "$SIZE" "$INPUT_SVG" -o "$OUT/icon-${SIZE}.png"
done

echo "Building .ico (16–256)..."
magick \
    "$OUT/icon-16.png" "$OUT/icon-32.png" "$OUT/icon-48.png" \
    "$OUT/icon-128.png" "$OUT/icon-256.png" \
    "$OUT/dir2site-icon.ico"

echo "Building .icns (macOS)..."
ICONSET="$OUT/dir2site-icon.iconset"
mkdir -p "$ICONSET"

cp "$OUT/icon-16.png"   "$ICONSET/icon_16x16.png"
cp "$OUT/icon-32.png"   "$ICONSET/icon_16x16@2x.png"
cp "$OUT/icon-32.png"   "$ICONSET/icon_32x32.png"
cp "$OUT/icon-64.png"   "$ICONSET/icon_32x32@2x.png"
cp "$OUT/icon-128.png"  "$ICONSET/icon_128x128.png"
cp "$OUT/icon-256.png"  "$ICONSET/icon_128x128@2x.png"
cp "$OUT/icon-256.png"  "$ICONSET/icon_256x256.png"
cp "$OUT/icon-512.png"  "$ICONSET/icon_256x256@2x.png"
cp "$OUT/icon-512.png"  "$ICONSET/icon_512x512.png"
cp "$OUT/icon-1024.png" "$ICONSET/icon_512x512@2x.png"

iconutil -c icns "$ICONSET" -o "$OUT/dir2site-icon.icns"
rm -rf "$ICONSET"

echo "Cleaning up intermediate PNGs..."
for SIZE in 16 32 48 64 128 256 512 1024; do
    rm "$OUT/icon-${SIZE}.png"
done

echo "Done:"
ls -lh "$OUT"/dir2site-icon.*

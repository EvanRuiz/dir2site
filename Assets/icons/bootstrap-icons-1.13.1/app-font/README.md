# bootstrap-icons.ttf

The same font as `../font/fonts/bootstrap-icons.woff`, decompressed to a plain sfnt.

Bootstrap Icons ships only `.woff` and `.woff2`. The generated site is happy with those, but the
desktop app renders through Skia, which reads TrueType and OpenType and not WOFF — so the icon
picker in the footer editor needs this form to show a glyph beside each name.

It is kept out of `font/fonts/` deliberately: that whole folder is copied into every generated
site, and a visitor has no use for a second copy of a font their browser already has as WOFF.

To regenerate after upgrading the vendored set, undo the WOFF container — its tables are just
zlib-deflated sfnt tables, so this is lossless and needs no font library:

    python3 scripts/woff2ttf.py \
      Assets/icons/bootstrap-icons-1.13.1/font/fonts/bootstrap-icons.woff \
      Assets/icons/bootstrap-icons-1.13.1/app-font/bootstrap-icons.ttf

Licensed under the MIT licence with the rest of the set — see `../LICENSE`.

"""Decompress a WOFF1 file back to the sfnt (TTF/OTF) it was made from.

WOFF1 is a thin container: an sfnt whose tables have each been zlib-deflated,
plus a directory recording both lengths. Undoing it is exact — the glyph data
is the same bytes the original font had — so this needs no font library and
introduces no third-party binary.

Spec: https://www.w3.org/TR/WOFF/
"""
import struct
import sys
import zlib

HEADER = ">IIIHHIHHIIIII"          # 44 bytes
ENTRY = ">IIIII"                   # 20 bytes per table


def convert(woff_path, out_path):
    data = open(woff_path, "rb").read()

    (sig, flavor, length, num_tables, _reserved, total_sfnt_size,
     _maj, _min, _meta_off, _meta_len, _meta_orig, _priv_off, _priv_len) = \
        struct.unpack(HEADER, data[:struct.calcsize(HEADER)])

    if sig != 0x774F4646:          # 'wOFF'
        raise SystemExit(f"not a WOFF file: signature {sig:#x}")

    entries = []
    pos = struct.calcsize(HEADER)
    for _ in range(num_tables):
        tag, offset, comp_len, orig_len, checksum = struct.unpack(
            ENTRY, data[pos:pos + struct.calcsize(ENTRY)])
        pos += struct.calcsize(ENTRY)

        raw = data[offset:offset + comp_len]
        # Equal lengths mean the table was stored uncompressed.
        table = raw if comp_len == orig_len else zlib.decompress(raw)
        if len(table) != orig_len:
            raise SystemExit(f"table {tag:#x}: expected {orig_len} bytes, got {len(table)}")
        entries.append((tag, checksum, table))

    entries.sort(key=lambda e: e[0])   # sfnt requires the directory be tag-sorted

    # sfnt header, then the table records, then the 4-byte-aligned table data.
    search_range = 1
    entry_selector = 0
    while search_range * 2 <= num_tables:
        search_range *= 2
        entry_selector += 1
    search_range *= 16
    range_shift = num_tables * 16 - search_range

    out = bytearray()
    out += struct.pack(">IHHHH", flavor, num_tables, search_range, entry_selector, range_shift)

    offset = len(out) + 16 * num_tables
    records = bytearray()
    body = bytearray()
    for tag, checksum, table in entries:
        records += struct.pack(">IIII", tag, checksum, offset, len(table))
        body += table
        padding = (-len(table)) % 4
        body += b"\0" * padding
        offset += len(table) + padding

    out += records + body

    if len(out) != total_sfnt_size:
        print(f"note: rebuilt {len(out)} bytes, header predicted {total_sfnt_size}", file=sys.stderr)

    open(out_path, "wb").write(out)
    print(f"wrote {out_path} ({len(out):,} bytes, {num_tables} tables)")


if __name__ == "__main__":
    convert(sys.argv[1], sys.argv[2])

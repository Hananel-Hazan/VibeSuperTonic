#!/usr/bin/env python3
"""Generate build/vibesupertonic.png — the product's only icon FILE.

Why a generator rather than a checked-in binary somebody once exported: this
mark already exists, in code, in the daemon. TrayPixmap draws it over D-Bus as
raw ARGB32 because StatusNotifierItem's other option resolves through the
desktop's icon theme, and a portable folder the user can move anywhere has no
install step that could register one — an icon that works on the developer's
machine and not on a copied folder is a hardcoded path by another name.

A .desktop entry has the opposite constraint: it can only name a file, and it
cannot run C#. So the mark has two implementations, and this is the seam where
they are kept honest — the geometry below is a transcription of
TrayPixmap.Covers, and the colour is its Speaking green. Change one, change both.

    python3 build/make-icon.py [size]

No dependencies. PIL is not installed on the build box and this is 60 lines.
"""
import struct, sys, zlib

SIZE = int(sys.argv[1]) if len(sys.argv) > 1 else 256
OUT = __file__.rsplit('/', 1)[0] + '/vibesupertonic.png'

# TrayPixmap.Colour(Speaking). The launcher has no state to report, so it takes
# the colour that means the product is doing its job rather than the muted grey
# that means idle.
R, G, B = 0x35, 0xB4, 0x5A

def covers(d):
    """TrayPixmap.Covers for Preparing: the ring plus its centre.

    The most icon-like of the three states — a bare ring reads as a loading
    spinner at launcher size, and a filled disc reads as nothing at all.
    """
    return (0.50 <= d <= 0.72) or d <= 0.26

SUB = 4                                   # supersampling; the tray uses 3 at 22px
centre = (SIZE - 1) / 2.0
unit = SIZE / 2.0
rows = []
for y in range(SIZE):
    row = bytearray([0])                  # PNG filter byte: none
    for x in range(SIZE):
        hits = 0
        for sy in range(SUB):
            for sx in range(SUB):
                px = x + (sx + 0.5) / SUB - 0.5
                py = y + (sy + 0.5) / SUB - 0.5
                d = ((px - centre) ** 2 + (py - centre) ** 2) ** 0.5 / unit
                if covers(d):
                    hits += 1
        a = 255 * hits // (SUB * SUB)
        row += bytes((R, G, B, a))        # RGBA8, straight alpha
    rows.append(bytes(row))

def chunk(tag, data):
    body = tag + data
    return struct.pack('>I', len(data)) + body + struct.pack('>I', zlib.crc32(body) & 0xffffffff)

png = (b'\x89PNG\r\n\x1a\n'
       + chunk(b'IHDR', struct.pack('>IIBBBBB', SIZE, SIZE, 8, 6, 0, 0, 0))
       + chunk(b'IDAT', zlib.compress(b''.join(rows), 9))
       + chunk(b'IEND', b''))
open(OUT, 'wb').write(png)
print(f"{OUT}  {SIZE}x{SIZE}  {len(png)} bytes")

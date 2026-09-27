#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
make_icon.py -- generate the Kiwix USB multi-resolution Windows icon.

Run:  python src/branding/make_icon.py
Out:  src/branding/kiwix.ico     10 entries, 32-bit RGBA, real alpha
      src/branding/preview.png   contact sheet, every size on light + dark
      (a short summary is printed; --verify re-reads kiwix.ico from disk)

Why this script exists instead of a checked-in .ico
--------------------------------------------------
The only icon the bundle used to ship was KiwixServe/kiwix.ico: the upstream
kiwi-bird logo, 3 sizes (16/32/48), pure black on white. On a dark shell the
white plate is a flashlight, and at 16x16 the bird is an unreadable smudge --
which is the whole reason this generator exists. Everything is drawn from
primitives here so the icon can be re-tuned without a design tool: there is no
cairosvg, no Inkscape and no ImageMagick in this environment, only Pillow.

Design
------
* Rounded-square "app badge" in the app's own dark palette, so the icon reads
  as part of the UI rather than pasted on top of it. Windows shows a rounded
  square with transparent corners natively, so no masking tricks are needed.
* A chunky geometric "K" on a normalised grid: vertical stem, one diagonal arm,
  one diagonal leg. It is a K and nothing else -- no book, no bookmark, no
  signal arcs. The brief was legibility first, and a K only survives 16px if it
  is a slab of blocks.
* The leg is accent green (#3DDC84) and the stem + arm are the app's body
  foreground (#E6E9F0). Green is therefore not a 2px dot that disappears; it is
  a third of the mark, and it still reads as green on a 16px grid.
* Slight vertical gradient with a soft top sheen, plus an ordered 4x4 Bayer
  dither (+/-1 level). The gradient spans about 15 levels over the badge height,
  which bands badly at 256px without the dither. No random noise is used, so
  the output is byte-for-byte reproducible.
* Optical weight compensation: small sizes get a proportionally bolder stroke
  (1.30x at 16px, 1.00x at 64px and up). A stroke that is optically correct at
  48px turns into a 1px grey smear at 16px.
* Sizes <= 32px snap the stem edges and the K's bounding box to the pixel grid
  so those edges land hard instead of straddling two half-lit pixels.

ICO container
-------------
The .ico is written by hand (ICONDIR + ICONDIRENTRY + BITMAPINFOHEADER +
bottom-up BGRA + 1bpp AND mask) rather than via Image.save(format="ICO").
That is deliberate: it is ~40 lines, it guarantees biBitCount=32 and
compression=BI_RGB on every entry, and it keeps the alpha channel intact,
which is exactly the property the old icon was missing.
"""

from __future__ import annotations

import argparse
import struct
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

# --------------------------------------------------------------------------
# Palette -- lifted from src/KiwixWinUI/App.xaml and MainWindow.xaml so the
# icon cannot drift away from the running app.
# --------------------------------------------------------------------------
BG_DEEP = (0x0C, 0x0F, 0x15)  # below the app background, badge bottom
BG_TOP = (0x24, 0x2B, 0x3C)  # card shade, lifted, badge top
GLYPH = (0xE6, 0xE9, 0xF0)  # body foreground
ACCENT = (0x3D, 0xDC, 0x84)  # accent green, "ok / running"
SHELL_LIGHT = (0xF0, 0xF0, 0xF0)  # Windows light taskbar
SHELL_DARK = (0x20, 0x20, 0x20)  # Windows dark taskbar
SHELL_APP = (0x12, 0x15, 0x1C)  # the app's own background

# SIZES = every size Windows actually asks for across DPI levels and surfaces.
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]

# Optical weight compensation, relative stroke weight per output size.
BOLD = {
    16: 1.30, 20: 1.24, 24: 1.18, 32: 1.11,
    40: 1.06, 48: 1.03, 64: 1.00, 96: 1.00, 128: 1.00, 256: 1.00,
}

SS = 8  # supersample factor; integer, so downscaling is an exact box filter
SHEEN = 34  # peak alpha of the top highlight, out of 255
BOUNCE = 22  # peak alpha of the green bounce along the bottom edge
ICO_NAME = "kiwix.ico"
PREVIEW_NAME = "preview.png"


# --------------------------------------------------------------------------
# K geometry, in normalised 0..1 badge coordinates.
#
#        x_stem_l        x_stem_r                 x_end
#   y_top  +--+              .
#         |  |              \  arm (foreground)
#         |  |               \
#   y_mid  |  +---------------+  <- the two diagonals meet; solid junction
#         |  |               /
#         |  |              /  leg (accent green)
#   y_bot  +--+             .
#
# The arm and leg are parallelograms with *vertical* thickness, so the stroke
# weight is controlled by one number (va) and the junction is always solid.
# --------------------------------------------------------------------------
STEM_X0 = 0.256
STEM_W = 0.132  # x 1.0 at full size
K_X1 = 0.748
K_Y0 = 0.185
K_Y1 = 0.815
VA = 0.182  # vertical thickness of the arm / leg, x 1.0
BADGE_INSET = 0.020
BADGE_RADIUS = 0.230


def _snap(v: float, size: int) -> float:
    """Snap a normalised coordinate onto the pixel grid (small sizes only)."""
    return round(v * size) / size


def k_geometry(size: int) -> dict:
    """Return the K polygons for `size`, with optical weight and pixel snapping."""
    w = BOLD[size]
    snap = size <= 32

    x0 = STEM_X0
    x1 = x0 + STEM_W * w
    y0, y1, x_end = K_Y0, K_Y1, K_X1
    va = VA * (1.0 + (w - 1.0) * 0.85)

    if snap:
        # Snap the four hard edges of the mark. Without this a stem sitting on
        # x=4.19 renders as two columns of 50% grey at 16px and the whole K
        # goes soft.
        x0 = _snap(x0, size)
        x1 = _snap(x1, size)
        y0 = _snap(y0, size)
        y1 = _snap(y1, size)
        x_end = _snap(x_end, size)

    ym = (y0 + y1) / 2.0
    half = va / 2.0

    # The two diagonals must meet the stem's right edge on either side of the
    # midline, so the K's counter opens *at* the stem. Attaching both bands to
    # the same edge of the stem (as in "both start at ym+half") makes the two
    # polygons overlap near the stem; the leg then paints a wedge over the arm
    # and the arm reads as a detached flake rather than a K.
    stem = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
    arm = [
        (x1, ym - half),      # outer, at the stem
        (x1, ym),             # inner, at the stem  <- counter apex
        (x_end, y0 + va),     # inner, at the cap
        (x_end, y0),          # outer, at the cap
    ]
    leg = [
        (x1, ym),             # inner, at the stem  <- counter apex
        (x1, ym + half),      # outer, at the stem
        (x_end, y1),          # outer, at the foot
        (x_end, y1 - va),     # inner, at the foot
    ]
    return {"stem": stem, "arm": arm, "leg": leg}


# --------------------------------------------------------------------------
# Rendering
# --------------------------------------------------------------------------
BAYER4 = [
    [0, 8, 2, 10],
    [12, 4, 14, 6],
    [3, 11, 1, 9],
    [15, 7, 13, 5],
]


def _dither(img: Image.Image) -> Image.Image:
    """Apply a deterministic ordered 4x4 Bayer dither of +/-1 level.

    ImageChops.add only adds, so the pattern is split into a positive and a
    negative plane and applied as add-then-subtract. A single tile centred on
    mid-grey would brighten the whole image by 128, which is exactly the bug
    this shape exists to avoid.
    """
    def tiled(plane):
        tile = Image.new("L", (4, 4))
        tile.putdata(plane)
        big = Image.new("L", (img.width, img.height))
        for y in range(0, img.height, 4):
            for x in range(0, img.width, 4):
                big.paste(tile, (x, y))
        return big.convert(img.mode)  # ImageChops requires identical modes

    offs = [(BAYER4[y][x] * 2) // 16 - 1 for y in range(4) for x in range(4)]
    pos = tiled([max(0, o) for o in offs])
    neg = tiled([max(0, -o) for o in offs])
    return ImageChops.subtract(ImageChops.add(img, pos), neg)


def _vgrad(w: int, h: int, top: tuple, bottom: tuple) -> Image.Image:
    strip = Image.new("RGB", (1, 256))
    px = strip.load()
    for y in range(256):
        t = y / 255.0
        px[0, y] = tuple(int(round(top[c] + (bottom[c] - top[c]) * t)) for c in range(3))
    return strip.resize((w, h), Image.BILINEAR)


def _fill_poly(size: int, polys) -> Image.Image:
    """Render normalised polygons into an 'L' coverage mask at `size` px."""
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    for pts in polys:
        d.polygon([(x * size, y * size) for x, y in pts], fill=255)
    return m


def _paint(dst: Image.Image, colour: tuple, mask: Image.Image) -> None:
    layer = Image.new("RGBA", dst.size, colour + (255,))
    dst.paste(layer, (0, 0), mask)


def _alpha_composite_mask(dst: Image.Image, colour: tuple, mask: Image.Image) -> None:
    """Composite a flat colour using `mask` as the per-pixel alpha (not binary)."""
    alpha = mask
    if alpha.mode != "L":
        alpha = alpha.convert("L")
    layer = Image.new("RGBA", dst.size, colour + (0,))
    layer.putalpha(alpha)
    dst.alpha_composite(layer)


def render(size: int) -> Image.Image:
    """Render one size, supersampled then box-filtered down to `size`."""
    n = size * SS
    geo = k_geometry(size)
    inset = _snap(BADGE_INSET, size) if size <= 32 else BADGE_INSET
    radius = max(1.0, BADGE_RADIUS * n)

    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))

    badge_box = [inset * n, inset * n, (1.0 - inset) * n, (1.0 - inset) * n]
    badge_mask = Image.new("L", (n, n), 0)
    ImageDraw.Draw(badge_mask).rounded_rectangle(badge_box, radius=radius, fill=255)

    # Soft drop shadow, large sizes only. At 16px it is sub-pixel and would only
    # eat into the badge's own contrast.
    if size >= 32:
        sh = Image.new("L", (n, n), 0)
        off = max(1, int(round(n * 0.020)))
        ImageDraw.Draw(sh).rounded_rectangle(
            [badge_box[0], badge_box[1] + off, badge_box[2], badge_box[3] + off],
            radius=radius, fill=150,
        )
        sh = sh.filter(ImageFilter.GaussianBlur(n * 0.018))
        _alpha_composite_mask(img, (0, 0, 0), sh)

    # Badge body: vertical gradient, dithered, clipped to the rounded square.
    grad = _dither(_vgrad(n, n, BG_TOP, BG_DEEP))
    img.paste(Image.merge("RGBA", (*grad.split(), badge_mask)), (0, 0))

    # Soft top sheen: a blurred ellipse, intersected with the badge so it cannot
    # spill onto the transparent corners.
    sheen = Image.new("L", (n, n), 0)
    ImageDraw.Draw(sheen).ellipse(
        [-n * 0.35, -n * 0.80, n * 1.35, n * 0.55], fill=SHEEN,
    )
    sheen = sheen.filter(ImageFilter.GaussianBlur(n * 0.085))
    _alpha_composite_mask(img, (255, 255, 255), ImageChops.multiply(sheen, badge_mask))

    # Faint accent bounce along the bottom inner edge, tying the badge to the
    # app's green without introducing a visible band.
    bounce = Image.new("L", (n, n), 0)
    ImageDraw.Draw(bounce).ellipse(
        [-n * 0.30, n * 0.62, n * 1.30, n * 1.55], fill=BOUNCE,
    )
    bounce = bounce.filter(ImageFilter.GaussianBlur(n * 0.10))
    _alpha_composite_mask(img, ACCENT, ImageChops.multiply(bounce, badge_mask))

    # The mark. A soft dark shadow behind it, at large sizes only.
    glyph_mask = _fill_poly(n, [geo["stem"], geo["arm"]])
    leg_mask = _fill_poly(n, [geo["leg"]])
    if size >= 32:
        gs = glyph_mask.filter(ImageFilter.GaussianBlur(n * 0.016))
        _alpha_composite_mask(img, (0, 0, 0), gs.point(lambda v: int(v * 0.34)))

    _paint(img, GLYPH, glyph_mask)
    _paint(img, ACCENT, leg_mask)

    return img.resize((size, size), Image.BOX)


# --------------------------------------------------------------------------
# ICO writer
# --------------------------------------------------------------------------
def write_ico(path: Path, frames: list) -> None:
    """Write an .ico by hand. Every entry is 32-bit BGRA + a 1bpp AND mask."""
    blobs = []
    for im in frames:
        size = im.size[0]
        assert im.size[0] == im.size[1], "square frames only"
        assert im.mode == "RGBA", f"frame {size} must be RGBA, got {im.mode}"

        # A 256px entry encodes as 0 in the directory's width/height bytes.
        w = 0 if size >= 256 else size
        # ICO stores the XOR bitmap bottom-up; PIL's raw encoder is top-down.
        rows = [im.crop((0, y, size, y + 1)).tobytes("raw", "BGRA") for y in range(size)]
        xor = b"".join(reversed(rows))

        # AND mask: 1bpp, rows padded to a 4-byte boundary, bottom-up.
        stride = ((size + 31) // 32) * 4
        alpha = im.getchannel("A").tobytes()
        mask_rows = []
        for y in reversed(range(size)):
            bits = bytearray(stride)
            for x in range(size):
                if alpha[y * size + x] == 0:
                    bits[x >> 3] |= 0x80 >> (x & 7)
            mask_rows.append(bytes(bits))
        and_mask = b"".join(mask_rows)

        dib = struct.pack(
            "<IiiHHIIiiII",
            40,          # biSize
            size,        # biWidth
            size * 2,    # biHeight: Xor + AndMask stacked
            1,           # biPlanes
            32,          # biBitCount
            0,           # biCompression = BI_RGB
            len(xor),    # biSizeImage
            0, 0,        # pixels per metre, unused
            0, 0,        # biClrUsed, biClrImportant
        )
        blobs.append((w, w, dib + xor + and_mask))

    offset = 6 + 16 * len(blobs)
    dir_bytes = struct.pack("<HHH", 0, 1, len(blobs))
    entries = b""
    for w, h, blob in blobs:
        entries += struct.pack(
            "<BBBBHHII", w, h, 0, 0, 1, 32, len(blob), offset,
        )
        offset += len(blob)

    path.write_bytes(dir_bytes + entries + b"".join(b[2] for b in blobs))


# --------------------------------------------------------------------------
# Contact sheet
# --------------------------------------------------------------------------
def _checker(w: int, h: int, a=(0xCC, 0xCC, 0xCC), b=(0xF2, 0xF2, 0xF2), step=8):
    im = Image.new("RGB", (w, h), a)
    d = ImageDraw.Draw(im)
    for y in range(0, h, step):
        for x in range(0, w, step):
            if ((x // step) + (y // step)) % 2:
                d.rectangle([x, y, x + step - 1, y + step - 1], fill=b)
    return im


def build_preview(frames: dict, out: Path) -> None:
    """Contact sheet: every size at 1:1 on light and dark, plus a pixel view.

    The 1:1 columns are the whole point -- whether the 16px render reads is
    decided at true size, so nothing here is resampled except the explicitly
    labelled nearest-neighbour "pixel view" used for the small sizes.
    """
    pad = 24
    label_w = 128
    gap = 16
    cell_max = max(SIZES)            # 1:1 panels are sized by their icon
    mag_max = 176                    # cap for the magnified pixel view
    header_h = 144
    row_gap = 14

    # Column pitch is fixed by the 256px row so every panel lines up.
    pitch = cell_max + gap
    x_light = pad + label_w
    x_dark = x_light + pitch
    x_mag = x_dark + pitch
    width = x_mag + cell_max + pad
    height = header_h + sum(max(s, min(mag_max, s * 4)) + row_gap for s in SIZES) + pad

    sheet = Image.new("RGB", (width, height), SHELL_APP)
    d = ImageDraw.Draw(sheet)
    f_hd = ImageFont.load_default(size=28)
    f_sm = ImageFont.load_default(size=14)
    f_md = ImageFont.load_default(size=17)

    d.text((pad, 14), "Kiwix USB \u00b7 Windows icon set", font=f_hd, fill=(0xE6, 0xE9, 0xF0))
    d.text((pad, 52), f"{len(SIZES)} sizes \u00b7 16/20/24/32/40/48/64/96/128/256 \u00b7 "
                      "every entry 32-bit RGBA with a real alpha channel",
           font=f_sm, fill=(0x8B, 0x93, 0xA7))

    # Palette swatches, taken straight from App.xaml / MainWindow.xaml.
    for i, (col, name) in enumerate([
        ((0x12, 0x15, 0x1C), "bg"), ((0x24, 0x2B, 0x3C), "badge top"),
        ((0x3D, 0xDC, 0x84), "accent"), ((0xE6, 0xE9, 0xF0), "glyph"),
    ]):
        x = pad + i * 122
        d.rectangle([x, 76, x + 16, 92], fill=col, outline=(0x3A, 0x40, 0x52))
        d.text((x + 22, 77), name, font=f_sm, fill=(0x8B, 0x93, 0xA7))

    # Column captions sit just above the rows, clear of the title block.
    for x, title in ((x_light, "1:1 on light"), (x_dark, "1:1 on dark"),
                     (x_mag, "pixel view \u00b7 nearest neighbour")):
        d.text((x + 1, header_h - 36), title, font=f_md, fill=(0xE6, 0xE9, 0xF0))
    d.text((x_mag + 1, header_h - 16), "checkerboard = transparent", font=f_sm, fill=(0x8B, 0x93, 0xA7))

    y = header_h
    for size in SIZES:
        im = frames[size]
        mag = min(mag_max, size * 4)
        row_h = max(size, mag)

        d.text((pad + 4, y + row_h // 2 - 12), f"{size}\u00d7{size}", font=f_md, fill=(0xE6, 0xE9, 0xF0))
        d.text((pad + 4, y + row_h // 2 + 8), f"{size} px", font=f_sm, fill=(0x8B, 0x93, 0xA7))

        for x, shell in ((x_light, SHELL_LIGHT), (x_dark, SHELL_DARK)):
            top = y + (row_h - size) // 2
            tile = Image.new("RGB", (size, size), shell)
            tile.paste(im, (0, 0), im)
            d.rectangle([x - 1, top - 1, x + size, top + size], outline=(0x33, 0x39, 0x47))
            sheet.paste(tile, (x, top))

        if size <= 64:
            zoom = max(1, mag // size)
            z = size * zoom
            top = y + (row_h - z) // 2
            tile = _checker(z, z)
            up = im.resize((z, z), Image.NEAREST)
            tile.paste(up, (0, 0), up)
            d.rectangle([x_mag - 1, top - 1, x_mag + z, top + z], outline=(0x33, 0x39, 0x47))
            sheet.paste(tile, (x_mag, top))
            d.text((x_mag + 2, y + row_h + 1), f"\u00d7{zoom}", font=f_sm, fill=(0x8B, 0x93, 0xA7))
        else:
            top = y + (row_h - size) // 2
            tile = _checker(size, size)
            up = im.resize((size, size), Image.NEAREST)
            tile.paste(up, (0, 0), up)
            d.rectangle([x_mag - 1, top - 1, x_mag + size, top + size], outline=(0x33, 0x39, 0x47))
            sheet.paste(tile, (x_mag, top))
            d.text((x_mag + 2, y + row_h + 1), "1:1", font=f_sm, fill=(0x8B, 0x93, 0xA7))

        y += row_h + row_gap

    sheet.save(out, "PNG", optimize=True)


# --------------------------------------------------------------------------
# Verification
# --------------------------------------------------------------------------
def parse_ico(path: Path) -> list:
    """Parse an .ico independently of Pillow, straight from the byte layout."""
    d = path.read_bytes()
    res, typ, cnt = struct.unpack("<HHH", d[:6])
    assert (res, typ) == (0, 1), "not an .ico"
    out = []
    for i in range(cnt):
        e = d[6 + 16 * i: 6 + 16 * (i + 1)]
        w, h, ncol, rsv, planes, bpp, size, off = struct.unpack("<BBBBHHII", e)
        body = d[off:off + size]
        biSize, biW, biH, biPlanes, biBitCount, biComp = struct.unpack("<IiiHHI", body[:20])
        # biHeight is the XOR bitmap and the AND mask stacked, so half of it is
        # the visible image. Using the full biHeight here points the mask
        # offset past the end of the entry.
        xor_bytes = biW * (biH // 2) * (biBitCount // 8)
        and_off = 40 + xor_bytes
        out.append({
            "w": w or 256, "h": h or 256,
            "dir_bpp": bpp, "dir_planes": planes,
            "dib_w": biW, "dib_h": biH // 2, "bitcount": biBitCount,
            "compression": biComp, "bytes": size,
            "and_mask_bytes": len(body) - and_off,
        })
    return out


def decode_entry(path: Path, index: int):
    """Decode one entry straight from the bytes, independently of Pillow.

    This exists to catch the two mistakes that are invisible in an in-memory
    render: writing the XOR bitmap top-down (the icon would show upside down)
    and dropping the real alpha in favour of the 1bpp AND mask.
    """
    d = path.read_bytes()
    cnt = struct.unpack("<HHH", d[:6])[2]
    w, h, _nc, _rsv, _pl, _bpp, size, off = struct.unpack(
        "<BBBBHHII", d[6 + 16 * index:22 + 16 * index])
    W = w or 256
    body = d[off:off + size]
    biW, biH = struct.unpack("<ii", body[4:12])
    biBitCount = struct.unpack("<H", body[14:16])[0]
    rows = biH // 2
    xor = body[40:40 + biW * rows * (biBitCount // 8)]
    stride = biW * 4
    # ICO rows run bottom-up and pixels are BGRA; flip and reorder to RGBA.
    out = []
    for y in range(rows):
        row = xor[y * stride:(y + 1) * stride]
        out.append(b"".join(
            bytes((row[x * 4 + 2], row[x * 4 + 1], row[x * 4], row[x * 4 + 3]))
            for x in range(biW)))
    im = Image.frombytes("RGBA", (biW, rows), b"".join(reversed(out)))
    andm = body[40 + len(xor):]
    astride = ((biW + 31) // 32) * 4
    alpha = im.getchannel("A").tobytes()
    mism = 0
    for y in range(rows):
        r = andm[(rows - 1 - y) * astride:(rows - y) * astride]
        for x in range(biW):
            bit = (r[x >> 3] >> (7 - (x & 7))) & 1
            if bit != (1 if alpha[y * biW + x] == 0 else 0):
                mism += 1
    return im, mism


def _orientation_ok(im: Image.Image) -> bool:
    """True if the mark is the right way up, judged from the image alone.

    The arm is desaturated near-white and sits above the accent-green leg, so
    the white centroid must be higher than the green centroid. Flipping the
    XOR bitmap top-down inverts that and fails here. (Comparing raw brightness
    between the top and bottom of the frame does not work: the badge gradient
    is darker at the bottom either way round.)
    """
    white, green = [], []
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, a = im.getpixel((x, y))
            if a < 128:
                continue
            if g > 120 and g - r > 40 and g - b > 20:
                green.append(y)
            elif (r + g + b) / 3 > 140 and max(r, g, b) - min(r, g, b) < 40:
                white.append(y)
    if not white or not green:
        return False
    return sum(white) / len(white) < sum(green) / len(green)


def verify_roundtrip(ico: Path, frames: dict) -> bool:
    ok = True
    print("\n" + "-" * 78)
    print("BYTE-LEVEL ROUND TRIP (decoded from the file, not from memory)")
    print("-" * 78)
    print(f"{'size':>6}  {'matches render':>15}  {'white above green':>18}  {'AND mask err':>13}")
    for i, size in enumerate(SIZES):
        im, mism = decode_entry(ico, i)
        same = im.tobytes() == frames[size].tobytes()
        upright = _orientation_ok(im)
        if not (same and upright and mism == 0):
            ok = False
        print(f"{size:>6}  {str(same):>15}  {str(upright):>18}  {mism:>13}")
    return ok


def alpha_stats(im: Image.Image) -> dict:
    a = im.getchannel("A")
    px = list(a.tobytes())
    n = len(px)
    opaque = sum(1 for v in px if v == 255)
    clear = sum(1 for v in px if v == 0)
    partial = n - opaque - clear
    return {
        "min": min(px), "max": max(px), "unique": len(set(px)),
        "opaque_pct": 100.0 * opaque / n, "clear_pct": 100.0 * clear / n,
        "partial": partial,
    }


def check_small(frame: Image.Image, size: int) -> list:
    """Assert the small renders are neither blank nor a solid block."""
    problems = []
    st = alpha_stats(frame)
    # The real failure this guards against is the old icon: a binary or fully
    # uniform alpha plane. A genuine render has fully transparent corners, fully
    # opaque badge, and a band of partially transparent antialiased pixels
    # between them, so min < max, more than two distinct values, and at least
    # one partial pixel.
    if st["min"] == st["max"]:
        problems.append(f"{size}: alpha is a single constant value ({st['min']})")
    if st["unique"] < 3:
        problems.append(f"{size}: alpha has no range (unique values = {st['unique']})")
    if st["min"] == 255:
        problems.append(f"{size}: every pixel is opaque, badge has no rounded corners")
    if st["partial"] < 1:
        problems.append(f"{size}: no antialiased edge pixels (alpha is binary)")
    if st["clear_pct"] < 0.2:
        problems.append(f"{size}: nothing is transparent, so this is a solid block")

    # Split the badge interior into glyph vs background by luminance and
    # require real separation. Fully opaque interior pixels only, so the
    # transparent surround cannot inflate either group.
    rgba = frame.load()
    n = size
    inset = max(1, int(round(n * BADGE_INSET)) + 1)
    glyph_bg = []
    for y in range(inset, n - inset):
        for x in range(inset, n - inset):
            r, g, b, a = rgba[x, y]
            if a >= 200:
                lum = 0.2126 * r + 0.7152 * g + 0.0722 * b
                glyph_bg.append((lum, r, g, b))
    if len(glyph_bg) < n * n * 0.25:
        problems.append(f"{size}: badge interior mostly transparent ({len(glyph_bg)} px)")
        return problems

    lums = sorted(p[0] for p in glyph_bg)
    lo, hi = lums[0], lums[-1]
    spread = hi - lo
    if spread < 90:
        problems.append(f"{size}: glyph/background contrast too low ({spread:.1f})")

    # The mark must occupy a sane share of the badge: a K that swallows the
    # badge is a blob, a K that vanishes is a speck.
    top = [p for p in glyph_bg if p[0] > lo + (hi - lo) * 0.55]
    frac = 100.0 * len(top) / len(glyph_bg)
    if not (8.0 <= frac <= 62.0):
        problems.append(f"{size}: glyph fills {frac:.1f}% of badge interior (want 8-62%)")

    # The K's counter (the wedge between arm and leg) must survive: sample the
    # right-hand side above and below the midpoint, both must be badge-coloured.
    mid = n // 2
    right = [n - inset - max(1, n // 8), n - inset - 1]
    def lum_at(x, y):
        r, g, b, a = rgba[x, y]
        return (0.2126 * r + 0.7152 * g + 0.0722 * b, a)
    for label, y in (("upper", max(inset, mid - max(1, n // 6))),
                     ("lower", min(n - inset - 1, mid + max(1, n // 6)))):
        vals = [lum_at(x, y) for x in range(right[0], right[1]) if lum_at(x, y)[1] >= 200]
        if not vals:
            problems.append(f"{size}: {label} right flank is empty")
        elif max(v[0] for v in vals) > lo + (hi - lo) * 0.5:
            problems.append(f"{size}: {label} right flank has no gap -> arm and leg merged")

    # Accent green must be present in enough pixels to be seen, not one stray dot.
    green = 0
    for y in range(n):
        for x in range(n):
            r, g, b, a = rgba[x, y]
            if a >= 200 and g > 150 and g - r > 45 and g - b > 20:
                green += 1
    if green < 3:
        problems.append(f"{size}: accent green effectively absent ({green} px)")
    return problems


def verify(ico: Path, preview: Path, frames: dict) -> bool:
    print("\n" + "=" * 78)
    print("VERIFIED " + str(ico))
    print("=" * 78)
    entries = parse_ico(ico)
    print(f"ICONDIR: reserved=0 type=1 count={len(entries)}   file={ico.stat().st_size:,} bytes\n")
    hdr = f"{'#':>2}  {'size':>9}  {'dir bpp':>7}  {'DIB bitcount':>12}  {'compress':>9}  {'bytes':>8}  {'AND mask':>9}"
    print(hdr)
    print("-" * len(hdr))
    ok = True
    for i, e in enumerate(entries):
        comp = {0: "BI_RGB"}.get(e["compression"], str(e["compression"]))
        print(f"{i:>2}  {e['w']:>4}x{e['h']:<4}  {e['dir_bpp']:>7}  {e['bitcount']:>12}  {comp:>9}  {e['bytes']:>8,}  {e['and_mask_bytes']:>9,}")
        if e["bitcount"] != 32 or e["compression"] != 0:
            ok = False
            print(f"      !! entry {i} is not 32-bit uncompressed RGBA")
        if (e["w"], e["h"]) != (e["dib_w"], e["dib_h"]):
            ok = False
            print(f"      !! entry {i} directory size disagrees with the DIB header")

    want = [(s, s) for s in SIZES]
    got = [(e["w"], e["h"]) for e in entries]
    print(f"\nsizes expected: {want}")
    print(f"sizes in file : {got}")
    if got != want:
        ok = False
        print("      !! size list mismatch")

    # Cross-check the bytes we wrote against Pillow's own reader.
    im = Image.open(ico)
    pil_sizes = sorted(s[0] for s in im.ico.sizes())
    if pil_sizes != sorted(SIZES):
        ok = False
        print(f"      !! Pillow reads {pil_sizes}")
    else:
        print(f"Pillow re-read all {len(pil_sizes)} sizes: OK")

    print("\n" + "-" * 78)
    print("ALPHA / CONTENT SANITY CHECK")
    print("-" * 78)
    print(f"{'size':>6}  {'alpha min':>9}  {'alpha max':>9}  {'uniq':>5}  {'opaque%':>7}  {'clear%':>7}  {'AA px':>6}  {'glyph%':>7}")
    for size in SIZES:
        f = frames[size]
        st = alpha_stats(f)
        rgba = f.load()
        inset = max(1, int(round(size * BADGE_INSET)) + 1)
        interior = [(0.2126 * rgba[x, y][0] + 0.7152 * rgba[x, y][1] + 0.0722 * rgba[x, y][2])
                    for y in range(inset, size - inset) for x in range(inset, size - inset)
                    if rgba[x, y][3] >= 200]
        lo, hi = min(interior), max(interior)
        g = 100.0 * sum(1 for v in interior if v > lo + (hi - lo) * 0.55) / len(interior)
        print(f"{size:>6}  {st['min']:>9}  {st['max']:>9}  {st['unique']:>5}  {st['opaque_pct']:>7.1f}  {st['clear_pct']:>7.1f}  {st['partial']:>6}  {g:>7.1f}")

    print("\n" + "-" * 78)
    print("SMALL-SIZE LEGIBILITY CHECK (16 / 20 / 24 / 32)")
    print("-" * 78)
    for size in (16, 20, 24, 32):
        problems = check_small(frames[size], size)
        if problems:
            ok = False
            for p in problems:
                print(f"  FAIL  {p}")
        else:
            print(f"  ok    {size}x{size}: non-uniform alpha, real glyph/background contrast, open counter, green present")

    ok = verify_roundtrip(ico, frames) and ok

    print("\n" + "-" * 78)
    print(f"preview.png: {preview.stat().st_size:,} bytes  {preview.parent / PREVIEW_NAME}")
    if preview.stat().st_size < 20000:
        ok = False
        print("      !! preview looks truncated")

    print("\n" + ("ALL CHECKS PASSED" if ok else "*** CHECKS FAILED ***"))
    return ok


# --------------------------------------------------------------------------
def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[1])
    ap.add_argument("--out-dir", type=Path, default=Path(__file__).resolve().parent)
    ap.add_argument("--verify-only", action="store_true", help="re-check existing output")
    args = ap.parse_args(argv)

    out_dir: Path = args.out_dir
    out_dir.mkdir(parents=True, exist_ok=True)
    ico = out_dir / ICO_NAME
    preview = out_dir / PREVIEW_NAME

    if args.verify_only:
        im = Image.open(ico)
        frames = {s: im.ico.getimage((s, s)).convert("RGBA") for s in SIZES}
        return 0 if verify(ico, preview, frames) else 1

    frames = {}
    for size in SIZES:
        frames[size] = render(size)
        print(f"rendered {size}x{size}")

    write_ico(ico, [frames[s] for s in SIZES])
    print(f"wrote {ico} ({ico.stat().st_size:,} bytes, {len(SIZES)} entries)")

    build_preview(frames, preview)
    print(f"wrote {preview} ({preview.stat().st_size:,} bytes)")

    return 0 if verify(ico, preview, frames) else 1


if __name__ == "__main__":
    sys.exit(main())

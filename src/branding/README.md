# Branding — Windows icon set

Everything in this directory is **generated**. `kiwix.ico` and `preview.png` are
build outputs; edit `make_icon.py` and re-run it, do not hand-edit the PNG or the
ICO.

```
src/branding/
├── make_icon.py     generator + self-verification (the only file you edit)
├── kiwix.ico        10 sizes, 32-bit RGBA, real alpha        (generated)
├── preview.png      contact sheet, every size on light + dark (generated)
└── README.md        this file
```

## Regenerating

```powershell
python src\branding\make_icon.py              # render, write, then verify
python src\branding\make_icon.py --verify-only   # re-check existing output only
python src\branding\make_icon.py --out-dir somewhere\else
```

Requires Python 3.11 and Pillow (tested with 3.11.15 / Pillow 12.2.0). Pillow is
the **only** dependency — there is no cairosvg, Inkscape or ImageMagick in this
environment, so the artwork is drawn from Pillow primitives (`rounded_rectangle`,
`ellipse`, `polygon`, `GaussianBlur`) and composited by hand. Text in the contact
sheet uses Pillow's bundled default font, so the script has no font dependency
either.

The script is **deterministic**: no `random`, no timestamps, no locale. The
gradient dither is a fixed 4x4 Bayer matrix. Re-running produces a byte-identical
`.ico` and `preview.png`, so a rebuild never shows up as a spurious diff.

## The design

A rounded-square badge carrying a chunky two-tone **K**.

| Role | Colour | Source |
| --- | --- | --- |
| Badge top | `#242B3C` | derived from the `#1B1F2A` card in `App.xaml` |
| Badge bottom | `#0C0F15` | below the `#0E1117` mono background, so the badge has depth |
| Glyph (stem + arm) | `#E6E9F0` | `App.xaml` `Body` foreground |
| Accent (leg) | `#3DDC84` | `MainWindow.xaml.cs` "ok / running" green |

The badge is the app's own dark palette rather than a white plate, so the icon
reads as part of a dark launcher instead of a flashlight on the taskbar. Windows
gives a rounded square with transparent corners natively, so no masking is needed.

### Why it survives 16x16

16x16 is the binding constraint, so the geometry was designed around it:

- **It is a K and nothing else.** No book, no bookmark, no signal arcs, no
  secondary detail. Anything that competes with the letterform at 16px is
  something the letterform cannot afford.
- **A thick stem and thick diagonals.** At 16x16 the stem is exactly 3px wide
  (columns 4–6) and each diagonal is 3.65px thick vertically, which is
  **3.09px measured perpendicular** at a 32° angle. A "correct" 1px hairline
  becomes a grey smear; nothing here is thinner than 3px at the smallest size.
- **The counter is a real V.** The wedge between the arm and the leg opens at
  the stem's midpoint, so the negative space is as legible as the strokes. The
  verifier asserts the counter is still open at 16/20/24/32 — if the arm and leg
  ever merged, the build fails rather than shipping a blob.
- **Pixel snapping at 16/20/24/32.** The stem edges and the mark's bounding box
  are snapped to the pixel grid, so those edges land hard instead of straddling
  two half-lit pixels. Only the diagonals are antialiased.
- **Optical weight compensation.** Stroke weight is scaled up as size goes down
  (1.30x at 16px, 1.00x at 64px and up). A stroke that looks right at 48px is
  optically too light at 16px.
- **The green is not a dot.** The accent green is the whole leg — roughly a
  third of the mark and 3.09px thick at 16x16 — so it cannot disappear. The
  verifier fails the build if fewer than 3 green pixels survive at any size.

The one design decision worth calling out: **the two diagonals attach to the
stem on opposite sides of the midline.** Attaching both bands to the same edge
makes them overlap near the stem, and since the leg paints last it then carves a
green wedge out of the arm — which reads as a detached flake, not a K. This is
the single easiest way to get this mark wrong.

Two subtler rendering notes:

- **The gradient is dithered.** The badge spans about 15 levels over its height,
  which bands visibly at 256px. A 4x4 ordered Bayer dither of ±1 level removes
  it. `ImageChops.add` can only add, so the pattern is split into a positive and
  a negative plane and applied as add-then-subtract; a tile centred on mid-grey
  would brighten the whole image by 128.
- **The drop shadow and glyph shadow are 32px and up only.** At 16px they are
  sub-pixel and would only eat into the badge's own contrast.

## Verified output

The script re-reads the file it just wrote and prints this. It is not a claim,
it is a check that fails the run.

```
ICONDIR: reserved=0 type=1 count=10   file=419,110 bytes

 #       size  dir bpp  DIB bitcount   compress     bytes   AND mask
 0    16x16         32            32     BI_RGB     1,128         64
 1    20x20         32            32     BI_RGB     1,720         80
 2    24x24         32            32     BI_RGB     2,440         96
 3    32x32         32            32     BI_RGB     4,264        128
 4    40x40         32            32     BI_RGB     6,760        320
 5    48x48         32            32     BI_RGB     9,640        384
 6    64x64         32            32     BI_RGB    16,936        512
 7    96x96         32            32     BI_RGB    38,056      1,152
 8   128x128        32            32     BI_RGB    67,624      2,048
 9   256x256        32            32     BI_RGB   270,376      8,192
```

Every entry is `biBitCount=32`, `biCompression=BI_RGB`, with a real alpha
channel: at 16x16 the alpha plane has 11 distinct levels (0, 64, 84, 92, 112,
199, 203, 231, 235, 251, 255) and the corner pixel is fully transparent.

### The checks the script runs

| Check | What it catches |
| --- | --- |
| ICONDIR / DIB agreement | a size that disagrees between directory and header |
| `biBitCount` and compression | an entry that silently lost its alpha |
| Pillow re-read of all 10 sizes | an entry the decoder cannot find |
| Alpha range, unique levels, partial pixels | a binary or uniform alpha plane |
| Glyph vs background luminance spread | a blank or solid-block render |
| Glyph coverage 8–62% of badge | a mark that vanished or swallowed the badge |
| Counter still open at 16/20/24/32 | arm and leg merged into a blob |
| ≥3 accent-green pixels | green disappearing at small sizes |
| Byte-level round trip | XOR bitmap written upside down (a real risk when hand-rolling the ICO) |
| AND mask vs alpha agreement | the 1bpp mask disagreeing with the alpha channel |

The orientation test is that white sits above green in the vertical centroid.
Its negative control is real: it returns `False` for a deliberately flipped
render, so it is not a check that always passes.

Independently confirmed outside the script: Windows' own GDI icon loader
(`System.Drawing.Icon`) opens the file, reports `Format32bppArgb` for 16/32/48,
and returns exact native entries for 16, 24, 32, 48, 64, 96 and 128.

## The .ico is written by hand

`write_ico()` emits `ICONDIR` + `ICONDIRENTRY` + `BITMAPINFOHEADER` + bottom-up
BGRA + a 1bpp AND mask, rather than calling `Image.save(format="ICO")`. It is
about 40 lines and it removes any question about whether Pillow's ICO plugin
preserves 32-bit alpha — which is the property that matters most here. The XOR
bitmap is written bottom-up as the format requires; the round-trip check exists
specifically because getting that backwards produces an upside-down icon that
still decodes without complaint.

## Not wired up yet

The generator and the assets are all that exist here. Two consumers are still
unwired, deliberately — this directory is assets only and nothing else in the
repo was touched:

- `src/KiwixWinUI/KiwixWinUI.csproj` sets no `ApplicationIcon`, so
  `KiwixWinUI.exe` still shows the generic Windows icon. Adding
  `<ApplicationIcon>..\branding\kiwix.ico</ApplicationIcon>` is the one-line
  change that fixes it.
- `src/launcher/winlauncher.c` embeds no icon resource, so `Kiwix.exe` has none.
  That needs an `.rc` file plus a `windres`/rc.exe step in `build-launcher.ps1`.

Until those exist, `kiwix.ico` is a droppable asset for either of them.

## Compromise worth knowing about

The 256x256 entry is uncompressed `BI_RGB` (270 KB of the 419 KB total). The
common modern alternative is a PNG-compressed 256 entry, which is much smaller —
but a PNG entry carries no `BITMAPINFOHEADER`, so its bit depth cannot be read
back and printed, and the whole point of the table above is that it can.
Uncompressed also means all ten entries have identical, inspectable structure.
A PNG 256 entry would be a size optimisation, not a correctness one; if the file
size ever matters, that is the lever.

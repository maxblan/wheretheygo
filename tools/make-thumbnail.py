#!/usr/bin/env python3
"""Draws dotnet/Properties/Thumbnail.png, the mod's card on PDX Mods.

The asset is generated rather than hand-painted so it stays honest: the band
colours are read from the SAME ramp the overlay uses (Assumptions.BandColourWarm
to BandColourCool, warm where nobody rides to cool where everybody does), and
the widths step the way BandView classes them. Change the ramp in Assumptions.cs
and re-run this; do not repaint the PNG by hand.

    python3 tools/make-thumbnail.py

Needs Pillow. Renders at 3x and downsamples, because PIL has no antialiased
stroke.
"""

import pathlib
import re

from PIL import Image, ImageDraw, ImageFont

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "dotnet" / "Properties" / "Thumbnail.png"
ASSUMPTIONS = ROOT / "dotnet" / "Common" / "Planning" / "Assumptions.cs"

W, H = 950, 500
SS = 3  # supersampling factor

BG_TOP = (18, 22, 30)
BG_BOTTOM = (28, 35, 47)
GRID = (255, 255, 255, 10)


def ramp_from_assumptions():
    """Read BandColourWarm/Cool out of Assumptions.cs so the art cannot drift."""
    text = ASSUMPTIONS.read_text(encoding="utf-8")

    def triple(name):
        m = re.search(
            rf"{name}\s*=\s*{{\s*([0-9.]+)f?\s*,\s*([0-9.]+)f?\s*,\s*([0-9.]+)f?\s*}}", text
        )
        if not m:
            raise SystemExit(f"{name} not found in Assumptions.cs")
        return tuple(round(float(g) * 255) for g in m.groups())

    return triple("BandColourWarm"), triple("BandColourCool")


WARM, COOL = ramp_from_assumptions()


def lerp(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def quad(p0, p1, p2, steps=64):
    """Quadratic bezier, the same shape BandGeometry gives a band."""
    out = []
    for i in range(steps + 1):
        t = i / steps
        u = 1 - t
        out.append(
            (
                u * u * p0[0] + 2 * u * t * p1[0] + t * t * p2[0],
                u * u * p0[1] + 2 * u * t * p1[1] + t * t * p2[1],
            )
        )
    return out


def arc_control(a, b, bow):
    """Control point that bows the band perpendicular to its chord, as the map does."""
    mx, my = (a[0] + b[0]) / 2, (a[1] + b[1]) / 2
    dx, dy = b[0] - a[0], b[1] - a[1]
    length = max((dx * dx + dy * dy) ** 0.5, 1e-6)
    nx, ny = -dy / length, dx / length
    return (mx + nx * bow * 2, my + ny * bow * 2)


# (end A, end B, bow, width class 0..3, carried share 0..1)
# Laid out to sit right of the title block, asymmetric on purpose: a real city's
# corridors converge on a centre, they do not form a tidy lens.
BANDS = [
    ((196, 436), (548, 132), -58, 3, 0.88),
    ((548, 132), (612, 330), 34, 2, 0.66),
    ((612, 330), (884, 210), -44, 3, 0.71),
    ((196, 436), (612, 330), 52, 2, 0.14),
    ((548, 132), (812, 104), 22, 1, 0.48),
    ((612, 330), (806, 438), 30, 2, 0.06),
    ((884, 210), (812, 104), -18, 1, 0.52),
    ((806, 438), (884, 210), -26, 1, 0.37),
    ((196, 436), (806, 438), 74, 1, 0.22),
    ((548, 132), (884, 210), -36, 2, 0.79),
]

WIDTHS = {0: 3, 1: 5, 2: 9, 3: 15}
NODES = [(196, 436), (548, 132), (612, 330), (884, 210), (812, 104), (806, 438)]


def font(name, size):
    for path in (
        f"/mnt/c/Windows/Fonts/{name}",
        f"/usr/share/fonts/truetype/lato/Lato-{'Bold' if 'sb' in name or 'bd' in name else 'Regular'}.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
    ):
        try:
            return ImageFont.truetype(path, size)
        except OSError:
            continue
    return ImageFont.load_default()


def main():
    img = Image.new("RGB", (W * SS, H * SS), BG_TOP)
    draw = ImageDraw.Draw(img, "RGBA")

    # Vertical gradient ground.
    for y in range(H * SS):
        draw.line(
            [(0, y), (W * SS, y)], fill=lerp(BG_TOP, BG_BOTTOM, y / (H * SS)), width=1
        )

    # The 32 m tile grid the coverage layer rasterises onto, barely there.
    for x in range(0, W, 38):
        draw.line([(x * SS, 0), (x * SS, H * SS)], fill=GRID, width=SS)
    for y in range(0, H, 38):
        draw.line([(0, y * SS), (W * SS, y * SS)], fill=GRID, width=SS)

    # Bands, heaviest last so the thick warm corridors sit on top.
    for a, b, bow, cls, carried in sorted(BANDS, key=lambda band: band[3]):
        colour = lerp(WARM, COOL, carried)
        pts = [(x * SS, y * SS) for x, y in quad(a, arc_control(a, b, bow), b)]
        width = WIDTHS[cls] * SS
        # Casing first, then the fill, the way BandRenderer darkens an outline.
        draw.line(pts, fill=(0, 0, 0, 90), width=width + 3 * SS, joint="curve")
        draw.line(pts, fill=colour + (235,), width=width, joint="curve")

    for x, y in NODES:
        r = 7 * SS
        draw.ellipse(
            [x * SS - r, y * SS - r, x * SS + r, y * SS + r],
            fill=(245, 248, 252, 230),
            outline=(18, 22, 30, 255),
            width=2 * SS,
        )

    # Scrim so the title stays legible over whatever the bands do behind it.
    scrim_w = int(W * 0.60) * SS
    for x in range(0, scrim_w):
        t = x / scrim_w
        alpha = int(242 * (1 - t) ** 2.4)
        draw.line([(x, 0), (x, H * SS)], fill=(11, 14, 20, alpha), width=1)

    img = img.resize((W, H), Image.LANCZOS)
    draw = ImageDraw.Draw(img, "RGBA")

    title = font("seguisb.ttf", 64)
    sub = font("segoeui.ttf", 25)
    small = font("segoeui.ttf", 17)

    draw.text((58, 150), "Where They Go", font=title, fill=(247, 250, 254))
    draw.text(
        (61, 228),
        "Where your city wants to go —",
        font=sub,
        fill=(196, 209, 226),
    )
    draw.text(
        (61, 260),
        "and how much of it you already carry.",
        font=sub,
        fill=(196, 209, 226),
    )

    # The ramp itself, labelled, because that is the mod's whole answer in one glance.
    bx, by, bw, bh = 62, 332, 300, 13
    for i in range(bw):
        draw.line(
            [(bx + i, by), (bx + i, by + bh)], fill=lerp(WARM, COOL, i / bw), width=1
        )
    draw.rectangle([bx, by, bx + bw, by + bh], outline=(255, 255, 255, 60))
    draw.text((bx, by + 24), "Nobody rides", font=small, fill=(168, 182, 201))
    right = "All carried"
    draw.text(
        (bx + bw - draw.textlength(right, font=small), by + 24),
        right,
        font=small,
        fill=(168, 182, 201),
    )

    OUT.parent.mkdir(parents=True, exist_ok=True)
    img.save(OUT, "PNG", optimize=True)
    print(f"wrote {OUT.relative_to(ROOT)} ({OUT.stat().st_size} bytes, {W}x{H})")
    print(f"ramp read from Assumptions.cs: warm {WARM} -> cool {COOL}")


if __name__ == "__main__":
    main()

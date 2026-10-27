"""Draws the iris sprite sets for Bionic Irises. Run: python make_irises.py

Each design is drawn as tiny pixel art (4x4 supersampled) into 512 x 512 layers that line up with
[NL] Facial Animation - Experimentals' eyeballs, one layer per eye side:

  <Design>/<Gender>/normal_{L,R}_{north,east,south,west}.png    the sprite in its own colours
  <Design>/<Gender>/normal_{L,R}_m_{north,east,south,west}.png  accent mask: red = tinted with the pawn's eye colour

Accent pixels are painted as light greys so the game's CutoutComplex shader multiplies them by the eye
colour; everything under black in the mask keeps the colour drawn here. Facing south both eyes show, each
in its own layer; facing east only the right eye shows, facing west only the left (mirrored).
"""
import math
import os
import struct
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "Textures", "Things", "Pawn", "BionicIrises")
SIZE = 512
SS = 4  # supersamples per pixel side

# Iris footprints measured from Facial Animation - Experimentals' "Normal" eyeballs (centre x, centre y,
# half width, half height, superellipse exponent). Every Experimentals eye type sits within a pixel or two.
FOOTPRINT = {
    ("Female", "south"): [(226.0, 279.0, 10.6, 11.6, 2.3), (285.0, 279.0, 10.6, 11.6, 2.3)],
    ("Female", "east"): (292.5, 278.5, 7.0, 12.0, 2.2),
    ("Male", "south"): [(226.5, 277.0, 10.1, 8.6, 2.8), (285.0, 277.0, 10.1, 8.6, 2.8)],
    ("Male", "east"): (292.0, 278.0, 6.6, 8.6, 2.5),
}


def hexc(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) / 255 for i in (0, 2, 4))


def mix(a, b, t):
    t = max(0.0, min(1.0, t))
    return tuple(x + (y - x) * t for x, y in zip(a, b))


def grey(v):
    return (v, v, v)


def lit(top, bottom, v):
    """Facial Animation lights irises from below: darker at the top edge, lighter at the bottom."""
    return mix(hexc(top), hexc(bottom), (v + 1) / 2)


def accent(v, lo=0.62):
    return grey(lo + (1 - lo) * (v + 1) / 2)


OUTLINE = hexc("#0b0c0e")
WHITE = hexc("#f4f6f8")


def glint(u, v, cx, cy, r):
    return (u - cx) ** 2 + (v - cy) ** 2 < r * r


# ---- designs: (u, v) in [-1, 1] across the footprint, v pointing down; return (rgb, is_accent) ----
def bionic(u, v, r, a):
    """Vanilla bionic eye: gunmetal bezel with four screws, eye-coloured ring, black camera lens with a sensor glow."""
    if r > 0.86:
        return OUTLINE, False
    if glint(u, v, 0.30, -0.36, 0.15) or glint(u, v, -0.36, 0.40, 0.07):
        return WHITE, False
    if r > 0.68:
        if r > 0.72 and any(abs(math.remainder(a - k, 2 * math.pi)) < 0.22 for k in (0.785, 2.356, 3.927, 5.498)):
            return hexc("#16191c"), False
        return lit("#2b3036", "#5d6670", v), False
    if r > 0.45:
        return accent(v), True
    if r < 0.15:
        return accent(v, 0.9), True
    if 0.29 < r < 0.35:
        return hexc("#262a30"), False
    return hexc("#0e1013"), False


def advanced(u, v, r, a):
    """EPOE advanced bionic eye: polished silver bezel, six-segment eye-coloured ring, hexagonal aperture round a glowing core."""
    if r > 0.86:
        return OUTLINE, False
    if glint(u, v, 0.32, -0.38, 0.14) or glint(u, v, -0.34, 0.42, 0.06):
        return WHITE, False
    if r > 0.72:
        return lit("#7a828a", "#d2d7dc", v), False
    if r > 0.67:
        return hexc("#24282d"), False
    if r > 0.42:
        if any(abs(math.remainder(a - k * math.pi / 3 - math.pi / 6, 2 * math.pi)) < 0.2 for k in range(6)):
            return hexc("#1d2025"), False
        return accent(v, 0.66), True
    if r < 0.12:
        return accent(v, 0.95), True
    if max(abs(u), abs(u) * 0.5 + abs(v) * 0.866) < 0.3:
        return hexc("#050607"), False
    return lit("#1f2328", "#434a53", v), False


def tactical(u, v, r, a):
    """EPOE tactical bionic eye: matte olive housing with range ticks, black glass, eye-coloured reticle."""
    if r > 0.86:
        return OUTLINE, False
    if glint(u, v, 0.40, -0.42, 0.11):
        return WHITE, False
    if r > 0.70:
        if r < 0.80 and ((abs(u) < 0.09 and v < 0) or (abs(v) < 0.09 and u > 0)):
            return hexc("#8d907c"), False
        return lit("#31332c", "#5b5f4e", v), False
    ring = 0.48 < r < 0.58
    cross = (abs(v) < 0.075 and 0.2 < abs(u) < 0.66) or (abs(u) < 0.075 and 0.2 < abs(v) < 0.66)
    if ring or cross or r < 0.09:
        return accent(v, 0.8), True
    return lit("#07090b", "#16191d", v), False


def archotech(u, v, r, a):
    """Archotech eye: gold rim, pearl iris with gold veins, luminous eye-coloured ring, four-point star pupil."""
    if r > 0.86:
        return hexc("#1d160b"), False
    if glint(u, v, 0.30, -0.40, 0.14):
        return WHITE, False
    if r > 0.69:
        return lit("#7e6026", "#e2bd60", v), False
    star = abs(u) ** 0.7 + abs(v) ** 0.7 < 0.30 ** 0.7
    if star:
        return hexc("#08070a"), False
    if 0.30 < r < 0.43:
        return accent(v, 0.78), True
    if r > 0.43 and any(abs(math.remainder(a - k * math.pi / 2 - math.pi / 4, 2 * math.pi)) < 0.13 for k in range(4)):
        return hexc("#b48f40"), False
    return lit("#c8c3b4", "#f3efe4", v), False


DESIGNS = {"Bionic": bionic, "AdvancedBionic": advanced, "TacticalBionic": tactical, "Archotech": archotech}


# ---- rasteriser ----------------------------------------------------------------------------
def blank():
    return [bytearray(SIZE * 4) for _ in range(SIZE)], [bytearray(SIZE * 3) for _ in range(SIZE)]


def draw(img, mask, design, cx, cy, rx, ry, p, mirror=False):
    rows, mrows = img, mask
    for py in range(int(cy - ry) - 2, int(cy + ry) + 3):
        for px in range(int(cx - rx) - 2, int(cx + rx) + 3):
            acc = [0.0, 0.0, 0.0]
            cov = tint = 0.0
            for sy in range(SS):
                for sx in range(SS):
                    u = (px + (sx + 0.5) / SS - cx) / rx
                    v = (py + (sy + 0.5) / SS - cy) / ry
                    if abs(u) ** p + abs(v) ** p > 1:
                        continue
                    if mirror:
                        u = -u
                    r = math.hypot(u, v)
                    rgb, is_acc = design(u, v, r, math.atan2(v, u))
                    acc = [s + c for s, c in zip(acc, rgb)]
                    cov += 1
                    tint += is_acc
            if not cov:
                continue
            n = SS * SS
            o = px * 4
            rows[py][o:o + 4] = bytes([round(c / cov * 255) for c in acc] + [round(cov / n * 255)])
            mrows[py][px * 3] = round(tint / cov * 255)


def save_png(path, rows, channels):
    raw = b"".join(b"\x00" + bytes(r) for r in rows)
    ct = 6 if channels == 4 else 2
    w = len(rows[0]) // channels

    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    with open(path, "wb") as fh:
        fh.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, len(rows), 8, ct, 0, 0, 0))
                 + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def preview(path):
    """About/Preview.png: the four irises enlarged, each tinted with a different eye colour."""
    eyes = [(0.25, 0.55, 0.95), (0.30, 0.78, 0.40), (0.95, 0.62, 0.18), (0.62, 0.40, 0.92)]
    w, h, bg = 960, 540, (24, 27, 31)
    canvas = [bytearray(bytes(bg) * w) for _ in range(h)]
    cx, cy, rx, ry, p = FOOTPRINT[("Female", "south")][1]
    x0, y0, cw, ch = int(cx - rx) - 1, int(cy - ry) - 1, int(2 * rx) + 3, int(2 * ry) + 3
    z = min(h * 3 // 5 // ch, w // (len(DESIGNS) * cw + len(DESIGNS) + 1))
    gap = (w - len(DESIGNS) * cw * z) // (len(DESIGNS) + 1)
    top = (h - ch * z) // 2
    for i, (design, eye) in enumerate(zip(DESIGNS.values(), eyes)):
        img, mask = blank()
        draw(img, mask, design, cx, cy, rx, ry, p)
        left = gap + i * (cw * z + gap)
        for y in range(ch):
            for x in range(cw):
                r, g, b, a = img[y0 + y][(x0 + x) * 4:(x0 + x) * 4 + 4]
                if a < 128:
                    continue
                m = mask[y0 + y][(x0 + x) * 3] / 255
                px = bytes(round(c * (1 - m + m * e)) for c, e in zip((r, g, b), eye))
                for yy in range(top + y * z, top + (y + 1) * z):
                    canvas[yy][(left + x * z) * 3:(left + (x + 1) * z) * 3] = px * z
    save_png(path, canvas, 3)


def build():
    count = 0
    for name, design in DESIGNS.items():
        for gender in ("Female", "Male"):
            d = os.path.join(OUT, name, gender)
            os.makedirs(d, exist_ok=True)
            layers = {(side, rot): blank() for side in "LR" for rot in ("north", "east", "south", "west")}
            for side, (cx, cy, rx, ry, p) in zip("LR", FOOTPRINT[(gender, "south")]):
                draw(*layers[(side, "south")], design, cx, cy, rx, ry, p)
            cx, cy, rx, ry, p = FOOTPRINT[(gender, "east")]
            draw(*layers[("R", "east")], design, cx, cy, rx, ry, p)
            draw(*layers[("L", "west")], design, SIZE - 1 - cx, cy, rx, ry, p, mirror=True)
            for (side, rot), (img, mask) in layers.items():
                save_png(os.path.join(d, f"normal_{side}_{rot}.png"), img, 4)
                save_png(os.path.join(d, f"normal_{side}_m_{rot}.png"), mask, 3)
                count += 2
    empty = os.path.join(OUT, "Empty")
    os.makedirs(empty, exist_ok=True)
    for rot in ("north", "east", "south", "west"):
        save_png(os.path.join(empty, f"normal_{rot}.png"), blank()[0], 4)
        count += 1
    preview(os.path.join(HERE, "..", "About", "Preview.png"))
    print(f"wrote {count} textures to {os.path.normpath(OUT)} and About/Preview.png")


if __name__ == "__main__":
    build()

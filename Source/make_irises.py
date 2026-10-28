"""Draws the iris sprite sets for Bionic Irises as pixel art. Run: python make_irises.py

Every design is pixel art at twice Facial Animation's resolution: 1024 x 1024 layers over its 512 x 512 faces,
one sample per pixel, so each pixel is a deliberate colour and edges stay hard. Each eye of
[NL] Facial Animation - Experimentals is drawn in its own layer, L or R, and every layer has two texture sets:

  <Design>/<Gender>/normal_{L,R}_{south,east,west}.png       the eye with its lights off
  <Design>/<Gender>/normal_{L,R}_glow_{south,east,west}.png  its lights in white, tinted with the eye colour in game
                                                             and drawn unlit so that they glow

Facing south L is the eye on the viewer's left. Like Facial Animation's own eye masks, the L layer shows its eye
facing east and the R layer facing west; a blank texture hides each layer in its other side view. West art is drawn
in east orientation, because the game draws west on a mirrored mesh.

Each iris is centred in the eye opening of Experimentals' Normal lids. Facial Animation's own irises sit toward the
nose instead, so that they look at a point in front of the face; these designs look straight ahead.
"""
import math
import os
import struct
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "Textures", "Things", "Pawn", "BionicIrises")
SCALE = 2  # pixels per Facial Animation texture pixel
SIZE = 512 * SCALE
BLANK = 4  # side of the transparent texture that hides a layer in one side view

# Eye openings of Experimentals' Normal lids, in Facial Animation's 512 px space with pixel edges on whole numbers,
# then each iris's size in pixels at SCALE and its superellipse exponent. Facing south the first centre is the eye
# drawn in the L layer.
FOOTPRINT = {
    ("Female", "south"): ([(222.5, 279.0), (289.5, 279.0)], 42, 46, 2.3),
    ("Female", "east"): ([(290.5, 278.5)], 28, 46, 2.2),
    ("Male", "south"): ([(224.5, 278.0), (288.5, 278.0)], 40, 34, 2.8),
    ("Male", "east"): ([(290.0, 278.5)], 26, 34, 2.5),
}


def hexc(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) / 255 for i in (0, 2, 4))


def palette(*colours):
    return [hexc(c) for c in colours]


OUTLINE = hexc("#0b0c0e")
WHITE = hexc("#f4f6f8")
GLINT = "glint"  # marks a highlight pixel: it stays white and no glow spills over it
BAYER = ((0, 8, 2, 10), (12, 4, 14, 6), (3, 11, 1, 9), (15, 7, 13, 5))


class Pixel:
    """One pixel of an iris being drawn: its normalised position (u right, v down, rim at 1) and drawing helpers."""

    def __init__(self, x, y, w, h, p):
        self.x, self.y, self.p = x, y, p
        self.hw, self.hh = w / 2, h / 2
        self.u = (x + 0.5 - self.hw) / self.hw
        self.v = (y + 0.5 - self.hh) / self.hh
        self.r = math.hypot(self.u, self.v)
        self.light = (self.v + 1) / 2  # Facial Animation lights irises from below

    @staticmethod
    def exponent(p, rho):
        """Rings take the silhouette's shape at the rim and round off toward the centre."""
        return 2 + (p - 2) * rho * rho

    def within(self, rho):
        q = self.exponent(self.p, rho)
        return (abs(self.u) ** q + abs(self.v) ** q) ** (1 / q) < rho

    def ring_point(self, rho, angle):
        """The normalised point at angle `angle` on the ring of radius rho."""
        q = self.exponent(self.p, rho)
        c, s = math.cos(angle), math.sin(angle)
        scale = rho / (abs(c) ** q + abs(s) ** q) ** (1 / q)
        return c * scale, s * scale

    def near(self, cu, cv, radius):
        """Within `radius` pixels of the normalised point (cu, cv)."""
        return math.hypot((self.u - cu) * self.hw, (self.v - cv) * self.hh) < radius

    def off_line(self, angle):
        """Distance in pixels from the line through the centre at `angle`."""
        return abs(-math.sin(angle) * self.u * self.hw + math.cos(angle) * self.v * self.hh)

    def shade(self, colours, t=None, dither=0.5):
        """A palette colour for brightness t (default: the light from below), with ordered dithering at the seams."""
        t = self.light if t is None else t
        b = (BAYER[self.y % 4][self.x % 4] + 0.5) / 16 - 0.5
        i = int(math.floor(t * (len(colours) - 1) + 0.5 + b * dither))
        return colours[max(0, min(len(colours) - 1, i))]

    def tube(self, lo, hi):
        """Glow level across a lit band: brightest along its middle, like a lamp tube."""
        d = abs(self.r - (lo + hi) / 2) / ((hi - lo) / 2)
        return 1.0 if d < 0.45 else 0.84 if d < 0.8 else 0.7


# ---- designs: each returns (base colour, glow) for one interior pixel. glow is None for unlit pixels, a level in
# (0, 1] for a light (its base colour is the lamp switched off), or GLINT for a specular highlight. ----
GUNMETAL = palette("#1f2328", "#2a2f35", "#353b42", "#434a52", "#556069")
SCREW = palette("#101215", "#59636d", "#8c96a0")
LENS_OFF = hexc("#1a2027")


def screw(px, cu, cv, colours):
    """A round screw head 4 px across, lit from the top left, or None if px is not on it."""
    d = math.hypot((px.u - cu) * px.hw, (px.v - cv) * px.hh)
    if d >= 2.1:
        return None
    if d >= 1.25:
        return colours[0]
    return colours[2] if px.u < cu and px.v < cv else colours[1]


def bionic(px):
    """Vanilla bionic eye: gunmetal bezel with four screws, eye-coloured ring, black camera lens with a sensor glow."""
    if px.near(0.14, -0.19, 2.6) or px.near(-0.19, 0.15, 1.2):
        return WHITE, GLINT
    if not px.within(0.72):
        for k in range(4):
            head = screw(px, *px.ring_point(0.84, math.pi / 4 + k * math.pi / 2), colours=SCREW)
            if head:
                return head, None
        if not px.within(0.93) and px.v > 0.25:
            return GUNMETAL[-1], None  # rim catching the light from below
        return px.shade(GUNMETAL), None
    if not px.within(0.68):
        return hexc("#14171a"), None
    if not px.within(0.46):
        return LENS_OFF, px.tube(0.46, 0.68)
    if px.within(0.15):
        return LENS_OFF, 1.0
    if not px.within(0.29) and px.within(0.36):
        return hexc("#262b31"), None
    return hexc("#0d0f12"), None


SILVER = palette("#5f6770", "#7b838c", "#99a1a8", "#b8bec4", "#d6dade")
HOUSING = palette("#1c2025", "#262b31", "#313740", "#3d444d")


def advanced(px):
    """EPOE advanced bionic eye: polished silver bezel, six-segment eye-coloured ring, hexagonal aperture round a glowing core."""
    if px.near(0.17, -0.25, 2.2) or px.near(-0.22, 0.18, 1.0):
        return WHITE, GLINT
    if not px.within(0.79):
        for k in range(6):
            cu, cv = px.ring_point(0.885, math.pi / 6 + k * math.pi / 3)
            if px.near(cu, cv, 1.1):
                return hexc("#3a4047"), None  # rivets in line with the spokes
        if px.within(0.86) and px.v > 0.3:
            return SILVER[-1], None  # polished inner lip
        return px.shade(SILVER), None
    if not px.within(0.73):
        return hexc("#20242a"), None
    if not px.within(0.43):
        if any(px.off_line(math.pi / 6 + k * math.pi / 3) < 0.95 for k in range(3)):
            return hexc("#1a1d22"), None  # spokes between the six segments
        return LENS_OFF, px.tube(0.43, 0.73)
    if px.within(0.12):
        return LENS_OFF, 1.0
    if max(abs(px.u), abs(px.u) * 0.5 + abs(px.v) * 0.866) < 0.3:
        return hexc("#050607"), None
    return px.shade(HOUSING), None


OLIVE = palette("#26281f", "#30332a", "#3c3f34", "#494d3f", "#585c4b")
GLASS = palette("#06080a", "#0a0d10", "#0e1115", "#13171b")
TICK = hexc("#8e917d")


def tactical(px):
    """EPOE tactical bionic eye: matte olive housing with range ticks, black glass, eye-coloured reticle."""
    if px.near(0.30, -0.31, 1.9) or px.near(-0.31, 0.29, 0.9):
        return WHITE, GLINT
    if not px.within(0.77):
        for angle, length in ((-math.pi / 2, 0.80), (0.0, 0.80), (-math.pi / 2 - 0.42, 0.86), (-math.pi / 2 + 0.42, 0.86),
                              (-0.42, 0.86), (0.42, 0.86)):
            ahead = px.u * math.cos(angle) + px.v * math.sin(angle) > 0
            if ahead and px.off_line(angle) < 0.75 and not px.within(length) and px.within(0.95):
                return TICK, None  # range ticks along the top and the outer side
        return px.shade(OLIVE, dither=0.8), None
    if not px.within(0.71):
        return hexc("#15170f"), None
    ring = px.within(0.585) and not px.within(0.49)
    arms = (px.off_line(0) < 1.0 and 0.2 < abs(px.u) < 0.68) or (px.off_line(math.pi / 2) < 1.0 and 0.2 < abs(px.v) < 0.68)
    if ring or arms or px.near(0, 0, 1.6):
        return hexc("#1b2026"), 1.0 if not ring else (0.84 if px.v < -0.3 else 1.0)
    return px.shade(GLASS, t=1 - px.r * 0.9 if px.v > 0 else px.light * 0.6, dither=0.7), None


GOLD = palette("#5b4318", "#7c5d25", "#9e7a33", "#c29a48", "#e0bb5f")
PEARL = palette("#b4ae9e", "#c5c0b1", "#d5d0c3", "#e4e0d5", "#f2eee3")
VEIN = hexc("#a9873c")


def archotech(px):
    """Archotech eye: gold rim, pearl iris with gold veins, luminous eye-coloured ring, four-point star pupil."""
    if px.near(0.38, -0.47, 2.0) or px.near(-0.42, 0.40, 0.9):
        return WHITE, GLINT
    if not px.within(0.72):
        if not px.within(0.9) and px.v > 0.3:
            return hexc("#f1d88a"), None  # bright lip of the gold rim
        return px.shade(GOLD), None
    if not px.within(0.69):
        return hexc("#6d5220"), None
    star = abs(px.u) ** 0.55 + abs(px.v) ** 0.55 < 0.34 ** 0.55
    if star:
        return hexc("#08070a"), None
    if not px.within(0.44):
        if any(px.off_line(math.pi / 4 + k * math.pi / 2) < 1.25 - 0.7 * (px.r - 0.44) / 0.25 for k in range(2)):
            return VEIN, None  # veins taper from the ring toward the rim
        return px.shade(PEARL, dither=0.7), None
    if not px.within(0.3):
        return hexc("#3a362e"), px.tube(0.3, 0.44)
    return px.shade(PEARL, t=0.55 + px.light * 0.45, dither=0.7), None


DESIGNS = {
    "Bionic": (bionic, OUTLINE),
    "AdvancedBionic": (advanced, OUTLINE),
    "TacticalBionic": (tactical, OUTLINE),
    "Archotech": (archotech, hexc("#1d160b")),
}
HALO = (0.45, 0.3)  # glow spilling past the lights: a solid pixel, then a checkerboard of fainter ones


# ---- rasteriser ----------------------------------------------------------------------------
def sprite(name, w, h, p):
    """Draws one iris, w x h pixels. Returns (base, glow) grids of RGBA tuples in 0..1, None where empty."""
    design, outline = DESIGNS[name]
    inside = [[Pixel(x, y, w, h, p).within(1.0) for x in range(w)] for y in range(h)]

    def filled(x, y):
        return 0 <= x < w and 0 <= y < h and inside[y][x]

    base = [[None] * w for _ in range(h)]
    glow = [[None] * w for _ in range(h)]
    lights, glints = set(), set()
    for y in range(h):
        for x in range(w):
            if not inside[y][x]:
                continue
            if not all(filled(x + dx, y + dy) for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))):
                base[y][x] = outline + (1.0,)
                continue
            rgb, level = design(Pixel(x, y, w, h, p))
            base[y][x] = rgb + (1.0,)
            if level == GLINT:
                glints.add((x, y))
            elif level is not None:
                glow[y][x] = (level, level, level, 1.0)
                lights.add((x, y))
    # The halo stays inside the rim and off the glints, in hard steps like the rest of the art.
    edge = set(lights)
    for step, (alpha, reach) in enumerate(zip(HALO, (((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)),
                                                     ((1, 0), (-1, 0), (0, 1), (0, -1))))):
        ring = set()
        for x, y in edge:
            for dx, dy in reach:
                n = (x + dx, y + dy)
                if n in lights or n in glints or not filled(*n) or base[n[1]][n[0]][:3] == outline or glow[n[1]][n[0]]:
                    continue
                ring.add(n)
        for x, y in ring:
            if step == 0 or (x + y) % 2 == 0:
                glow[y][x] = (1.0, 1.0, 1.0, alpha)
        edge = ring
    return base, glow


def canvas(size, rgb):
    """A transparent RGBA canvas whose hidden colour is rgb, so filtering at the edges never darkens or greys."""
    pixel = bytes(round(c * 255) for c in rgb) + b"\x00"
    return [bytearray(pixel * size) for _ in range(size)]


def paste(rows, grid, left, top):
    for y, line in enumerate(grid):
        row = rows[top + y]
        for x, px in enumerate(line):
            if px is not None:
                o = (left + x) * 4
                row[o:o + 4] = bytes(round(c * 255) for c in px)


def save_png(path, rows, channels=4):
    raw = b"".join(b"\x00" + bytes(r) for r in rows)
    ct = 6 if channels == 4 else 2
    w = len(rows[0]) // channels

    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    with open(path, "wb") as fh:
        fh.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, len(rows), 8, ct, 0, 0, 0))
                 + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def placed(centre, w, h):
    """Top-left pixel of a w x h sprite centred on a point in Facial Animation's 512 px space."""
    cx, cy = centre
    return int(round(cx * SCALE - w / 2)), int(round(cy * SCALE - h / 2))


def preview(path):
    """About/Preview.png: the four irises enlarged, each lit with a different eye colour, by day and by night."""
    eyes = [(0.25, 0.55, 0.95), (0.30, 0.85, 0.45), (1.0, 0.62, 0.18), (0.70, 0.45, 1.0)]
    w, h, bg = 960, 540, (0.09, 0.10, 0.12)
    rows = [bytearray(bytes(round(c * 255) for c in bg) * w) for _ in range(h)]
    _, sw, sh, p = FOOTPRINT[("Female", "south")]
    z = min((h - 60) // (2 * sh), (w - 100) // (len(DESIGNS) * sw))
    gap = (w - len(DESIGNS) * sw * z) // (len(DESIGNS) + 1)
    vgap = (h - 2 * sh * z) // 3
    for i, (name, eye) in enumerate(zip(DESIGNS, eyes)):
        base, glow = sprite(name, sw, sh, p)
        left = gap + i * (sw * z + gap)
        for row, dark in enumerate((1.0, 0.2)):  # night darkens everything but the lights
            top = vgap + row * (sh * z + vgap)
            for y in range(sh):
                for x in range(sw):
                    b, g = base[y][x], glow[y][x]
                    if b is None:
                        continue
                    rgb = [c * dark for c in b[:3]]
                    if g is not None:
                        rgb = [c * (1 - g[3]) + gc * e * g[3] for c, gc, e in zip(rgb, g[:3], eye)]
                    px = bytes(round(c * 255) for c in rgb)
                    for yy in range(top + y * z, top + (y + 1) * z):
                        rows[yy][(left + x * z) * 3:(left + (x + 1) * z) * 3] = px * z
    save_png(path, rows, 3)


def layer(grid, centre, w, h, hidden):
    rows = canvas(SIZE, hidden)
    paste(rows, grid, *placed(centre, w, h))
    return rows


def build():
    count = 0
    for name in DESIGNS:
        for gender in ("Female", "Male"):
            d = os.path.join(OUT, name, gender)
            os.makedirs(d, exist_ok=True)
            centres, w, h, p = FOOTPRINT[(gender, "south")]
            south = sprite(name, w, h, p)
            (side_centre,), sw, sh, sp = FOOTPRINT[(gender, "east")]
            side_view = sprite(name, sw, sh, sp)
            for kind, hidden, index in (("", OUTLINE, 0), ("_glow", (1, 1, 1), 1)):
                blank = canvas(BLANK, hidden)
                side_layer = layer(side_view[index], side_centre, sw, sh, hidden)
                for side, centre in zip("LR", centres):
                    prefix = os.path.join(d, f"normal_{side}{kind}_")
                    save_png(prefix + "south.png", layer(south[index], centre, w, h, hidden))
                    save_png(prefix + "east.png", side_layer if side == "L" else blank)
                    save_png(prefix + "west.png", side_layer if side == "R" else blank)
                    count += 3
    preview(os.path.join(HERE, "..", "About", "Preview.png"))
    print(f"wrote {count} textures to {os.path.normpath(OUT)} and About/Preview.png")


if __name__ == "__main__":
    build()

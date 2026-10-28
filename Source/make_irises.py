"""Draws the iris sprites for Bionic Irises as pixel art, fitted to [NL] Facial Animation - Experimentals' Normal eyes.

Run: python make_irises.py [folder of Facial Animation - Experimentals]   (defaults to the Steam workshop copy)

Experimentals' Normal eyeball textures are only read: every iris takes the outline and position of the Normal eye of
its gender, whatever the pawn's eye type, so a bionic eye looks where a natural one does, and an outer line as heavy as
the one Facial Animation draws round that eye, measured all the way round. The designs are pixel art at twice Facial
Animation's resolution, one sample per pixel, so each pixel is a deliberate colour and edges stay hard. Facing south
round parts are drawn round, with the housing filling the rest of the eye (the male eye is wider than it is tall); seen
side-on they are foreshortened.

<Design>/<Gender>/ holds, per eye layer (L or R):

  normal_{L,R}_{south,east}.png       the eye with its lights off
  normal_{L,R}_glow_{south,east}.png  its lights in white, tinted with the eye colour in game and drawn unlit

Facing south L is the eye on the viewer's left. _east is the eye seen side-on: the L layer shows it facing east and
the R layer facing west, which the game mirrors. Each texture covers a small rectangle of the face, and the game draws
it on the matching piece of the head mesh; 1.6/Defs/BionicIrisLayouts.xml, written here too, records those rectangles.
"""
import math
import os
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, "..")
OUT = os.path.join(ROOT, "Textures", "Things", "Pawn", "BionicIrises")
LAYOUT = os.path.join(ROOT, "1.6", "Defs", "BionicIrisLayouts.xml")
PREVIEW = os.path.join(ROOT, "About", "Preview.png")
EXPERIMENTALS = r"C:\Program Files (x86)\Steam\steamapps\workshop\content\294100\2581693737"
NORMAL_EYE = ("Textures", "Things", "Pawn", "FA-Experimentals", "Eyes", "Normal")
SCALE = 2  # pixels per Facial Animation texture pixel
PAD = 2  # clear pixels around every iris texture
GENDERS = ("Female", "Male")
VIEWS = (("L", "south"), ("R", "south"), ("side", "east"))
LINE_DARK = 0.12  # Facial Animation's outer line is the band darker than this inside the edge of its eyeball
LINE_SPREAD = 15  # degrees either side over which the measured line width is averaged


# ---- PNG ---------------------------------------------------------------------------------
def read_png(path):
    """Coverage (alpha) and brightness (mean of red, green and blue) of an 8-bit, non-interlaced PNG, as rows of
    floats in 0..1."""
    with open(path, "rb") as fh:
        data = fh.read()
    pos, idat = 8, b""
    while pos < len(data):
        length, kind = struct.unpack(">I4s", data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        if kind == b"IHDR":
            w, h, depth, ctype, _, _, interlace = struct.unpack(">IIBBBBB", body)
        elif kind == b"IDAT":
            idat += body
        pos += 12 + length
    channels = {0: 1, 2: 3, 4: 2, 6: 4}.get(ctype)
    if depth != 8 or interlace or channels is None:
        raise ValueError(f"{path}: only 8-bit, non-interlaced grey, RGB or RGBA images are read")
    raw, stride, prev, i = zlib.decompress(idat), w * channels, bytearray(w * channels), 0
    coverage, brightness = [], []
    colours = 3 if channels >= 3 else 1
    for _ in range(h):
        kind, line = raw[i], bytearray(raw[i + 1:i + 1 + stride])
        i += 1 + stride
        for x in range(stride):
            a = line[x - channels] if x >= channels else 0
            b = prev[x]
            c = prev[x - channels] if x >= channels else 0
            if kind == 1:
                line[x] = (line[x] + a) & 0xff
            elif kind == 2:
                line[x] = (line[x] + b) & 0xff
            elif kind == 3:
                line[x] = (line[x] + (a + b) // 2) & 0xff
            elif kind == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 0xff
        coverage.append([line[x * channels + channels - 1] / 255 if channels in (2, 4) else 1.0 for x in range(w)])
        brightness.append([sum(line[x * channels:x * channels + colours]) / (255 * colours) for x in range(w)])
        prev = line
    return coverage, brightness


def save_png(path, rows, channels=4):
    raw = b"".join(b"\x00" + bytes(r) for r in rows)
    ct = 6 if channels == 4 else 2
    w = len(rows[0]) // channels

    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    with open(path, "wb") as fh:
        fh.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, len(rows), 8, ct, 0, 0, 0))
                 + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


# ---- Experimentals' eyes -----------------------------------------------------------------------
class Shape:
    """One Normal eye at SCALE: the pixels inside its outline (cut where the game cuts Experimentals' texture, at its
    bilinear half coverage), its centre, and by angle the distance to the rim and the width of its outer line."""

    def __init__(self, texture, half, round_parts):
        """texture: (coverage, brightness) of a 512 px eyeball; half: None, or 'L'/'R' to keep one side of the face;
        round_parts: scale the design equally across and down, rather than to the eye's width and height."""
        self.coverage, self.brightness = texture
        self.half = half
        covered = [(x, y) for y, row in enumerate(self.coverage) for x, a in enumerate(row) if a > 0 and self.side(x)]
        x0, x1 = (min(x for x, _ in covered) - 1) * SCALE, (max(x for x, _ in covered) + 2) * SCALE
        y0, y1 = (min(y for _, y in covered) - 1) * SCALE, (max(y for _, y in covered) + 2) * SCALE
        self.pixels = {(x, y) for y in range(y0, y1) for x in range(x0, x1)
                       if self.sample(self.coverage, (x + 0.5) / SCALE, (y + 0.5) / SCALE) >= 0.5}
        self.x0 = min(x for x, _ in self.pixels)
        self.x1 = max(x for x, _ in self.pixels) + 1
        self.y0 = min(y for _, y in self.pixels)
        self.y1 = max(y for _, y in self.pixels) + 1
        self.cx, self.cy = (self.x0 + self.x1) / 2, (self.y0 + self.y1) / 2
        angles = [2 * math.pi * k / 360 for k in range(360)]
        self.rim = [self.march(a) for a in angles]
        widths = [self.line_width(a, r) for a, r in zip(angles, self.rim)]
        self.line = [sum(widths[(k + j) % 360] for j in range(-LINE_SPREAD, LINE_SPREAD + 1)) / (2 * LINE_SPREAD + 1)
                     for k in range(360)]
        self.inner = [r - w for r, w in zip(self.rim, self.line)]
        half_w = max(r * abs(math.cos(a)) for r, a in zip(self.inner, angles))
        half_h = max(r * abs(math.sin(a)) for r, a in zip(self.inner, angles))
        self.sx, self.sy = (min(half_w, half_h),) * 2 if round_parts else (half_w, half_h)

    def side(self, x):
        return self.half is None or (x < 256) == (self.half == "L")

    def sample(self, rows, x, y):
        """Bilinear value at a point in 512 px space, as the game filters it."""
        x, y = x - 0.5, y - 0.5
        i, j = math.floor(x), math.floor(y)
        fx, fy = x - i, y - j

        def at(a, b):
            return rows[b][a] if 0 <= a < len(rows) and 0 <= b < len(rows) and self.side(a) else 0
        return (at(i, j) * (1 - fx) + at(i + 1, j) * fx) * (1 - fy) + (at(i, j + 1) * (1 - fx) + at(i + 1, j + 1) * fx) * fy

    def at(self, rows, angle, d):
        return self.sample(rows, (self.cx + math.cos(angle) * d) / SCALE, (self.cy + math.sin(angle) * d) / SCALE)

    def march(self, angle):
        """Distance in pixels from the centre to the rim along `angle`."""
        d = 0.0
        while self.at(self.coverage, angle, d + 0.1) >= 0.5:
            d += 0.1
        return max(d, 0.5)

    def line_width(self, angle, rim):
        """Width in pixels of Facial Animation's dark outer line inside the rim along `angle`."""
        d = rim
        while d > 0 and self.at(self.brightness, angle, d) < LINE_DARK:
            d -= 0.1
        return min(max(rim - d, 1.0), rim / 3)

    def by_angle(self, values, angle):
        k = (angle % (2 * math.pi)) / (2 * math.pi) * 360
        i, f = int(k) % 360, k - int(k)
        return values[i] * (1 - f) + values[(i + 1) % 360] * f

    def rho(self, dx, dy):
        """0 at the centre to 1 at the inner edge of the outer line: round (to the design's scale) toward the centre,
        following the eye's outline toward the line."""
        round_rho = math.hypot(dx / self.sx, dy / self.sy)
        edge_rho = math.hypot(dx, dy) / self.by_angle(self.inner, math.atan2(dy, dx))
        return round_rho + (edge_rho - round_rho) * min(1.0, max(0.0, (edge_rho - 0.55) / 0.4))


# ---- pixel art ---------------------------------------------------------------------------
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
    """One pixel of an iris: its place in the design (u right, v down, in units of the design's scale; rho 0 at the
    centre to 1 at the outer line) and drawing helpers."""

    def __init__(self, shape, x, y):
        self.shape, self.x, self.y = shape, x, y
        self.dx, self.dy = x + 0.5 - shape.cx, y + 0.5 - shape.cy
        self.sx, self.sy = shape.sx, shape.sy
        self.u, self.v = self.dx / self.sx, self.dy / self.sy
        self.rho = shape.rho(self.dx, self.dy)
        self.on_line = math.hypot(self.dx, self.dy) >= shape.by_angle(shape.inner, math.atan2(self.dy, self.dx))
        self.light = (self.v + 1) / 2  # Facial Animation lights irises from below

    def within(self, rho):
        return self.rho < rho

    def ring_distance(self, rho, angle):
        """Distance in pixels from the centre to the ring rho along `angle`."""
        lo, hi = 0.0, self.shape.by_angle(self.shape.inner, angle)
        c, s = math.cos(angle), math.sin(angle)
        for _ in range(24):
            mid = (lo + hi) / 2
            if self.shape.rho(c * mid, s * mid) < rho:
                lo = mid
            else:
                hi = mid
        return lo

    def ring_point(self, rho, angle):
        """The point (u, v) at angle `angle` on the ring rho."""
        d = self.ring_distance(rho, angle)
        return d * math.cos(angle) / self.sx, d * math.sin(angle) / self.sy

    def band(self, lo, angle):
        """Width in pixels of the band from ring lo out to the outer line, along `angle`."""
        return self.shape.by_angle(self.shape.inner, angle) - self.ring_distance(lo, angle)

    def near(self, cu, cv, radius):
        """Within `radius` pixels of the point (cu, cv)."""
        return math.hypot((self.u - cu) * self.sx, (self.v - cv) * self.sy) < radius

    def off_line(self, angle):
        """Distance in pixels from the line through the centre at `angle`."""
        return abs(-math.sin(angle) * self.dx + math.cos(angle) * self.dy)

    def shade(self, colours, t=None, dither=0.5):
        """A palette colour for brightness t (default: the light from below), with ordered dithering at the seams."""
        t = self.light if t is None else t
        b = (BAYER[self.y % 4][self.x % 4] + 0.5) / 16 - 0.5
        i = int(math.floor(t * (len(colours) - 1) + 0.5 + b * dither))
        return colours[max(0, min(len(colours) - 1, i))]

    def tube(self, lo, hi):
        """Glow level across a lit band: brightest along its middle, like a lamp tube."""
        d = abs(self.rho - (lo + hi) / 2) / ((hi - lo) / 2)
        return 1.0 if d < 0.45 else 0.84 if d < 0.8 else 0.7


# ---- designs: each returns (base colour, glow) for one pixel inside the outer line. glow is None for unlit pixels, a
# level in (0, 1] for a light (its base colour is the lamp switched off), or GLINT for a specular highlight. ----
GUNMETAL = palette("#1f2328", "#2a2f35", "#353b42", "#434a52", "#556069")
SCREW = palette("#101215", "#59636d", "#8c96a0")
LENS_OFF = hexc("#1a2027")


def screw(px, cu, cv, colours):
    """A round screw head 4 px across, lit from the top left, or None if px is not on it."""
    d = math.hypot((px.u - cu) * px.sx, (px.v - cv) * px.sy)
    if d >= 2.1:
        return None
    if d >= 1.25:
        return colours[0]
    return colours[2] if px.u < cu and px.v < cv else colours[1]


def bionic(px):
    """Vanilla bionic eye: gunmetal bezel with four screws, eye-coloured ring, black camera lens with a sensor glow."""
    if px.near(0.16, -0.22, 2.6) or px.near(-0.2, 0.18, 1.2):
        return WHITE, GLINT
    if not px.within(0.7):
        for k in range(4):
            angle = math.pi / 4 + k * math.pi / 2
            if px.band(0.7, angle) >= 5:
                head = screw(px, *px.ring_point(0.85, angle), colours=SCREW)
                if head:
                    return head, None
        if not px.within(0.92) and px.v > 0.25:
            return GUNMETAL[-1], None  # rim catching the light from below
        return px.shade(GUNMETAL), None
    if not px.within(0.64):
        return hexc("#14171a"), None
    if not px.within(0.42):
        return LENS_OFF, px.tube(0.42, 0.64)
    if px.within(0.14):
        return LENS_OFF, 1.0
    if not px.within(0.27) and px.within(0.33):
        return hexc("#262b31"), None
    return hexc("#0d0f12"), None


SILVER = palette("#5f6770", "#7b838c", "#99a1a8", "#b8bec4", "#d6dade")
HOUSING = palette("#1c2025", "#262b31", "#313740", "#3d444d")


def advanced(px):
    """EPOE advanced bionic eye: polished silver bezel, six-segment eye-coloured ring, hexagonal aperture round a glowing core."""
    if px.near(0.17, -0.27, 2.2) or px.near(-0.22, 0.2, 1.0):
        return WHITE, GLINT
    if not px.within(0.76):
        for k in range(6):
            angle = math.pi / 6 + k * math.pi / 3
            if px.band(0.76, angle) >= 4 and px.near(*px.ring_point(0.88, angle), radius=1.1):
                return hexc("#3a4047"), None  # rivets in line with the spokes
        if px.within(0.84) and px.v > 0.3:
            return SILVER[-1], None  # polished inner lip
        return px.shade(SILVER), None
    if not px.within(0.7):
        return hexc("#20242a"), None
    if not px.within(0.42):
        if any(px.off_line(math.pi / 6 + k * math.pi / 3) < 0.95 for k in range(3)):
            return hexc("#1a1d22"), None  # spokes between the six segments
        return LENS_OFF, px.tube(0.42, 0.7)
    if px.within(0.12):
        return LENS_OFF, 1.0
    if max(abs(px.u), abs(px.u) * 0.5 + abs(px.v) * 0.866) < 0.29:
        return hexc("#050607"), None
    return px.shade(HOUSING), None


OLIVE = palette("#26281f", "#30332a", "#3c3f34", "#494d3f", "#585c4b")
GLASS = palette("#06080a", "#0a0d10", "#0e1115", "#13171b")
TICK = hexc("#8e917d")


def tactical(px):
    """EPOE tactical bionic eye: matte olive housing with range ticks, black glass, eye-coloured reticle."""
    if px.near(0.3, -0.33, 1.9) or px.near(-0.31, 0.3, 0.9):
        return WHITE, GLINT
    if not px.within(0.74):
        for angle, length in ((-math.pi / 2, 0.8), (0.0, 0.8), (-math.pi / 2 - 0.42, 0.87), (-math.pi / 2 + 0.42, 0.87),
                              (-0.42, 0.87), (0.42, 0.87)):
            ahead = px.dx * math.cos(angle) + px.dy * math.sin(angle) > 0
            if ahead and px.band(0.74, angle) >= 3.5 and px.off_line(angle) < 0.75 and not px.within(length):
                return TICK, None  # range ticks along the top and the outer side
        return px.shade(OLIVE, dither=0.8), None
    if not px.within(0.68):
        return hexc("#15170f"), None
    ring = px.within(0.57) and not px.within(0.47)
    arms = (px.off_line(0) < 1.0 and 0.2 < abs(px.u) < 0.66) or (px.off_line(math.pi / 2) < 1.0 and 0.2 < abs(px.v) < 0.66)
    if ring or arms or px.near(0, 0, 1.6):
        return hexc("#1b2026"), 1.0 if not ring else (0.84 if px.v < -0.3 else 1.0)
    return px.shade(GLASS, t=1 - px.rho * 0.9 if px.v > 0 else px.light * 0.6, dither=0.7), None


GOLD = palette("#5b4318", "#7c5d25", "#9e7a33", "#c29a48", "#e0bb5f")
PEARL = palette("#b4ae9e", "#c5c0b1", "#d5d0c3", "#e4e0d5", "#f2eee3")
VEIN = hexc("#a9873c")


def archotech(px):
    """Archotech eye: gold rim, pearl iris with gold veins, luminous eye-coloured ring, four-point star pupil."""
    if px.near(0.36, -0.46, 2.0) or px.near(-0.4, 0.4, 0.9):
        return WHITE, GLINT
    if not px.within(0.71):
        if not px.within(0.9) and px.v > 0.3:
            return hexc("#f1d88a"), None  # bright lip of the gold rim
        return px.shade(GOLD), None
    if not px.within(0.67):
        return hexc("#6d5220"), None
    if abs(px.u) ** 0.55 + abs(px.v) ** 0.55 < 0.33 ** 0.55:
        return hexc("#08070a"), None  # four-point star pupil
    if not px.within(0.44):
        if any(px.off_line(math.pi / 4 + k * math.pi / 2) < 1.25 - 0.7 * (px.rho - 0.44) / 0.23 for k in range(2)):
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


def sprite(name, shape):
    """Draws one design inside an eye's outline. Returns {pixel: (base rgba, glow rgba or None)} in face pixels."""
    design, outline = DESIGNS[name]
    inside = shape.pixels
    art, lights, glints = {}, set(), set()
    for x, y in inside:
        px = Pixel(shape, x, y)
        if px.on_line or not all(n in inside for n in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1))):
            art[(x, y)] = [outline + (1.0,), None]
            continue
        rgb, level = design(px)
        art[(x, y)] = [rgb + (1.0,), None]
        if level == GLINT:
            glints.add((x, y))
        elif level is not None:
            art[(x, y)][1] = (level, level, level, 1.0)
            lights.add((x, y))
    # The halo stays inside the outer line and off the glints, in hard steps like the rest of the art.
    edge = set(lights)
    for step, (alpha, reach) in enumerate(zip(HALO, (((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)),
                                                     ((1, 0), (-1, 0), (0, 1), (0, -1))))):
        ring = set()
        for x, y in edge:
            for dx, dy in reach:
                n = (x + dx, y + dy)
                if n in art and n not in lights and n not in glints and art[n][0][:3] != outline and art[n][1] is None:
                    ring.add(n)
        for x, y in ring:
            if step == 0 or (x + y) % 2 == 0:
                art[(x, y)][1] = (1.0, 1.0, 1.0, alpha)
        edge = ring
    return art


def texture(art, region, index, hidden):
    """One iris texture: the region of the face, transparent in `hidden` colour so filtering never darkens edges."""
    x0, y0, x1, y1 = region
    clear = bytes(round(c * 255) for c in hidden) + b"\x00"
    rows = [bytearray(clear * (x1 - x0)) for _ in range(y1 - y0)]
    for (x, y), layers in art.items():
        px = layers[index]
        if px is not None:
            rows[y - y0][(x - x0) * 4:(x - x0) * 4 + 4] = bytes(round(c * 255) for c in px)
    return rows


# ---- output ------------------------------------------------------------------------------
def write_layout(regions):
    lines = ['<?xml version="1.0" encoding="utf-8"?>',
             "<!-- Written by Source/make_irises.py: the rectangle of Facial Animation's 512 px face that each iris",
             "     texture covers, as top-left corner and size in pixels. Facing south \"left\" is the eye on the viewer's",
             "     left; \"side\" is the eye seen facing east, mirrored facing west. -->",
             "<Defs>"]
    for gender in GENDERS:
        (lx0, ly0, lx1, ly1), (rx0, ry0, _, _), (sx0, sy0, sx1, sy1) = (regions[(gender, v)] for v, _ in VIEWS)
        lines += ["  <BionicIrises.BionicIrisLayoutDef>",
                  f"    <defName>BionicIrisLayout_{gender}</defName>",
                  f"    <gender>{gender}</gender>",
                  f"    <left>({lx0 / SCALE:g},{ly0 / SCALE:g})</left>",
                  f"    <right>({rx0 / SCALE:g},{ry0 / SCALE:g})</right>",
                  f"    <frontSize>({(lx1 - lx0) / SCALE:g},{(ly1 - ly0) / SCALE:g})</frontSize>",
                  f"    <side>({sx0 / SCALE:g},{sy0 / SCALE:g})</side>",
                  f"    <sideSize>({(sx1 - sx0) / SCALE:g},{(sy1 - sy0) / SCALE:g})</sideSize>",
                  "  </BionicIrises.BionicIrisLayoutDef>"]
    lines.append("</Defs>")
    with open(LAYOUT, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines) + "\n")


def preview(path, shape):
    """About/Preview.png: the four irises enlarged, each lit with a different eye colour, by day and by night."""
    eyes = [(0.25, 0.55, 0.95), (0.30, 0.85, 0.45), (1.0, 0.62, 0.18), (0.70, 0.45, 1.0)]
    w, h, bg = 960, 540, (0.09, 0.10, 0.12)
    rows = [bytearray(bytes(round(c * 255) for c in bg) * w) for _ in range(h)]
    sw, sh = shape.x1 - shape.x0, shape.y1 - shape.y0
    z = min((h - 60) // (2 * sh), (w - 100) // (len(DESIGNS) * sw))
    gap = (w - len(DESIGNS) * sw * z) // (len(DESIGNS) + 1)
    vgap = (h - 2 * sh * z) // 3
    for i, (name, eye) in enumerate(zip(DESIGNS, eyes)):
        art = sprite(name, shape)
        left = gap + i * (sw * z + gap)
        for row, dark in enumerate((1.0, 0.2)):  # night darkens everything but the lights
            top = vgap + row * (sh * z + vgap)
            for (x, y), (b, g) in art.items():
                rgb = [c * dark for c in b[:3]]
                if g is not None:
                    rgb = [c * (1 - g[3]) + gc * e * g[3] for c, gc, e in zip(rgb, g[:3], eye)]
                px = bytes(round(c * 255) for c in rgb)
                for yy in range(top + (y - shape.y0) * z, top + (y - shape.y0 + 1) * z):
                    rows[yy][(left + (x - shape.x0) * z) * 3:(left + (x - shape.x0 + 1) * z) * 3] = px * z
    save_png(path, rows, 3)


def build(root):
    shapes, regions = {}, {}
    for gender in GENDERS:
        folder = os.path.join(root, *NORMAL_EYE, gender)
        south = read_png(os.path.join(folder, "normal_south.png"))
        east = read_png(os.path.join(folder, "normal_east.png"))
        shapes[gender] = {"L": Shape(south, "L", True), "R": Shape(south, "R", True), "side": Shape(east, None, False)}
        for view, _ in VIEWS:
            s = shapes[gender][view]
            regions[(gender, view)] = (s.x0 - PAD, s.y0 - PAD, s.x1 + PAD, s.y1 + PAD)
        # both eyes facing south share one size, so the game needs a single one
        (lx0, ly0, lx1, ly1), (rx0, ry0, rx1, ry1) = regions[(gender, "L")], regions[(gender, "R")]
        w, h, top = max(lx1 - lx0, rx1 - rx0), max(ly1, ry1) - min(ly0, ry0), min(ly0, ry0)
        regions[(gender, "L")] = (lx1 - w, top, lx1, top + h)  # grow outward, away from the nose
        regions[(gender, "R")] = (rx0, top, rx0 + w, top + h)
    count = 0
    for name in DESIGNS:
        for gender in GENDERS:
            d = os.path.join(OUT, name, gender)
            os.makedirs(d, exist_ok=True)
            for view, rot in VIEWS:
                art = sprite(name, shapes[gender][view])
                for side in (("L", "R") if view == "side" else (view,)):
                    for kind, index, hidden in (("", 0, OUTLINE), ("_glow", 1, (1, 1, 1))):
                        save_png(os.path.join(d, f"normal_{side}{kind}_{rot}.png"), texture(art, regions[(gender, view)], index, hidden))
                        count += 1
    write_layout(regions)
    preview(PREVIEW, shapes["Female"]["L"])
    print(f"wrote {count} textures to {os.path.normpath(OUT)}, the layout and the preview")


if __name__ == "__main__":
    build(sys.argv[1] if len(sys.argv) > 1 else EXPERIMENTALS)

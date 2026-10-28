import math
import os
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, "..")
OUT = os.path.join(ROOT, "Textures", "Things", "Pawn", "BionicIrises")
LAYOUT = os.path.join(ROOT, "1.6", "Defs", "BionicIrisLayouts.xml")
EXPERIMENTALS = r"C:\Program Files (x86)\Steam\steamapps\workshop\content\294100\2581693737"
NORMAL_EYE = ("Textures", "Things", "Pawn", "FA-Experimentals", "Eyes", "Normal")
SS = 4
PAD = 1
GENDERS = ("Female", "Male")
VIEWS = (("L", "south"), ("R", "south"), ("side", "east"))
LINE_DARK = 0.12
LINE_SPREAD = 15
GLINT_GLOW = 0.5
OWN_LINE = 0.86


def read_png(path):
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


class Eye:
    def __init__(self, texture, half):
        self.coverage, self.brightness = texture
        self.half = half
        covered = [(x, y) for y, row in enumerate(self.coverage) for x, a in enumerate(row) if a > 0 and self.side(x)]
        self.box = (min(x for x, _ in covered) - 1, min(y for _, y in covered) - 1,
                    max(x for x, _ in covered) + 2, max(y for _, y in covered) + 2)
        points = [(x + (sx + 0.5) / SS, y + (sy + 0.5) / SS) for y in range(self.box[1], self.box[3])
                  for x in range(self.box[0], self.box[2]) for sy in range(SS) for sx in range(SS)]
        points = [p for p in points if self.inside(*p)]
        n = len(points)
        self.cx = sum(x for x, _ in points) / n
        self.cy = sum(y for _, y in points) / n
        sxx = sum((x - self.cx) ** 2 for x, _ in points) / n
        syy = sum((y - self.cy) ** 2 for _, y in points) / n
        sxy = sum((x - self.cx) * (y - self.cy) for x, y in points) / n
        spread = math.sqrt(((sxx - syy) / 2) ** 2 + sxy ** 2)
        major = 2 * math.sqrt((sxx + syy) / 2 + spread)
        minor = 2 * math.sqrt((sxx + syy) / 2 - spread)
        tilt = 0.5 * math.atan2(2 * sxy, sxx - syy)
        if abs(math.cos(tilt)) >= abs(math.sin(tilt)):
            self.half_w, self.half_h, self.tilt = major, minor, tilt
        else:
            self.half_w, self.half_h, self.tilt = minor, major, tilt - math.copysign(math.pi / 2, tilt)
        c, s = math.cos(self.tilt), math.sin(self.tilt)
        self.reach_x = math.hypot(self.half_w * c, self.half_h * s)
        self.reach_y = math.hypot(self.half_w * s, self.half_h * c)
        angles = [2 * math.pi * k / 360 for k in range(360)]
        self.rim = [self.march(a) for a in angles]
        widths = [self.line_width(a, r) for a, r in zip(angles, self.rim)]
        line = [sum(widths[(k + j) % 360] for j in range(-LINE_SPREAD, LINE_SPREAD + 1)) / (2 * LINE_SPREAD + 1)
                for k in range(360)]
        self.inner = [r - w for r, w in zip(self.rim, line)]
        self.fit = 1.0
        edge = sorted(math.hypot(*self.design_point(self.cx + math.cos(a) * d, self.cy + math.sin(a) * d))
                      for a, d in zip(angles, self.inner))
        self.fit = OWN_LINE / edge[len(edge) // 2]

    def side(self, x):
        return self.half is None or (x < 256) == (self.half == "L")

    def sample(self, rows, x, y):
        x, y = x - 0.5, y - 0.5
        i, j = math.floor(x), math.floor(y)
        fx, fy = x - i, y - j

        def at(a, b):
            return rows[b][a] if 0 <= a < len(rows) and 0 <= b < len(rows) and self.side(a) else 0
        return (at(i, j) * (1 - fx) + at(i + 1, j) * fx) * (1 - fy) + (at(i, j + 1) * (1 - fx) + at(i + 1, j + 1) * fx) * fy

    def inside(self, x, y):
        return self.sample(self.coverage, x, y) >= 0.5

    def ray(self, rows, angle, d):
        return self.sample(rows, self.cx + math.cos(angle) * d, self.cy + math.sin(angle) * d)

    def march(self, angle):
        d = 0.0
        while self.ray(self.coverage, angle, d + 0.02) >= 0.5:
            d += 0.02
        return d

    def line_width(self, angle, rim):
        d = rim
        while d > 0 and self.ray(self.brightness, angle, d) < LINE_DARK:
            d -= 0.02
        return min(max(rim - d, 0.5), rim / 3)

    def on_line(self, x, y):
        dx, dy = x - self.cx, y - self.cy
        k = (math.atan2(dy, dx) % (2 * math.pi)) / (2 * math.pi) * 360
        i, f = int(k) % 360, k - int(k)
        return math.hypot(dx, dy) >= self.inner[i] * (1 - f) + self.inner[(i + 1) % 360] * f

    def design_point(self, x, y):
        dx, dy = (x - self.cx) * self.fit, (y - self.cy) * self.fit
        c, s = math.cos(self.tilt), math.sin(self.tilt)
        across, down = (dx * c + dy * s) / self.half_w, (-dx * s + dy * c) / self.half_h
        return across * c - down * s, across * s + down * c

    def screen_point(self, x, y):
        return (x - self.cx) * self.fit / self.reach_x, (y - self.cy) * self.fit / self.reach_y


def hexc(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) / 255 for i in (0, 2, 4))


def mix(a, b, t):
    t = max(0.0, min(1.0, t))
    return tuple(x + (y - x) * t for x, y in zip(a, b))


def lit(top, bottom, v):
    return mix(hexc(top), hexc(bottom), (v + 1) / 2)


def accent(v, lo=0.62):
    return LAMP_OFF, lo + (1 - lo) * (v + 1) / 2


OUTLINE = hexc("#0b0c0e")
WHITE = hexc("#f4f6f8")
LAMP_OFF = hexc("#1a2027")
GLINT = "glint"


def glint(su, sv, cx, cy, r):
    return (su - cx) ** 2 + (sv - cy) ** 2 < r * r


def bionic(u, v, r, a, su, sv, glints=True):
    if glints and (glint(su, sv, 0.30, -0.36, 0.15) or glint(su, sv, -0.36, 0.40, 0.07)):
        return WHITE, GLINT
    if r > 0.68:
        if r > 0.72 and any(abs(math.remainder(a - k, 2 * math.pi)) < 0.22 for k in (0.785, 2.356, 3.927, 5.498)):
            return hexc("#16191c"), None
        return lit("#2b3036", "#5d6670", sv), None
    if r > 0.45:
        return accent(sv)
    if r < 0.15:
        return accent(sv, 0.9)
    if 0.29 < r < 0.35:
        return hexc("#262a30"), None
    return hexc("#0e1013"), None


def advanced(u, v, r, a, su, sv, glints=True):
    if glints and (glint(su, sv, 0.32, -0.38, 0.14) or glint(su, sv, -0.34, 0.42, 0.06)):
        return WHITE, GLINT
    if r > 0.72:
        return lit("#7a828a", "#d2d7dc", sv), None
    if r > 0.67:
        return hexc("#24282d"), None
    if r > 0.42:
        if any(abs(math.remainder(a - k * math.pi / 3 - math.pi / 6, 2 * math.pi)) < 0.2 for k in range(6)):
            return hexc("#1d2025"), None
        return accent(sv, 0.66)
    if r < 0.12:
        return accent(sv, 0.95)
    if max(abs(u), abs(u) * 0.5 + abs(v) * 0.866) < 0.3:
        return hexc("#050607"), None
    return lit("#1f2328", "#434a53", sv), None


def tactical(u, v, r, a, su, sv, glints=True):
    if glints and glint(su, sv, 0.40, -0.42, 0.11):
        return WHITE, GLINT
    if r > 0.70:
        if r < 0.80 and ((abs(u) < 0.09 and v < 0) or (abs(v) < 0.09 and u > 0)):
            return hexc("#8d907c"), None
        return lit("#31332c", "#5b5f4e", sv), None
    ring = 0.48 < r < 0.58
    cross = (abs(v) < 0.075 and 0.2 < abs(u) < 0.66) or (abs(u) < 0.075 and 0.2 < abs(v) < 0.66)
    if ring or cross or r < 0.09:
        return accent(sv, 0.8)
    return lit("#07090b", "#16191d", sv), None


def archotech(u, v, r, a, su, sv, glints=True):
    if glints and glint(su, sv, 0.30, -0.40, 0.14):
        return WHITE, GLINT
    if r > 0.69:
        return lit("#7e6026", "#e2bd60", sv), None
    if abs(u) ** 0.7 + abs(v) ** 0.7 < 0.30 ** 0.7:
        return hexc("#08070a"), None
    if 0.30 < r < 0.43:
        return hexc("#3a362e"), accent(sv, 0.78)[1]
    if r > 0.43 and any(abs(math.remainder(a - k * math.pi / 2 - math.pi / 4, 2 * math.pi)) < 0.13 for k in range(4)):
        return hexc("#b48f40"), None
    return lit("#c8c3b4", "#f3efe4", sv), None


DESIGNS = {"Bionic": (bionic, OUTLINE), "AdvancedBionic": (advanced, OUTLINE),
           "TacticalBionic": (tactical, OUTLINE), "Archotech": (archotech, hexc("#1d160b"))}


def draw(name, eye, region):
    design, outline = DESIGNS[name]
    x0, y0, x1, y1 = region
    w, h = x1 - x0, y1 - y0
    n = SS * SS
    cells = {}
    for y in range(y0, y1):
        for x in range(x0, x1):
            cover, colour, light, level = 0, [0.0, 0.0, 0.0], 0, 0.0
            for sy in range(SS):
                for sx in range(SS):
                    px, py = x + (sx + 0.5) / SS, y + (sy + 0.5) / SS
                    if not eye.inside(px, py):
                        continue
                    cover += 1
                    if eye.on_line(px, py):
                        rgb = outline
                    else:
                        u, v = eye.design_point(px, py)
                        spot = (u, v, math.hypot(u, v), math.atan2(v, u)) + eye.screen_point(px, py)
                        rgb, glow = design(*spot)
                        if glow == GLINT:
                            under = design(*spot, glints=False)[1]
                            if under is not None:
                                light += GLINT_GLOW
                                level += GLINT_GLOW * under
                        elif glow is not None:
                            light += 1
                            level += glow
                    colour = [c + d for c, d in zip(colour, rgb)]
            if cover:
                cells[(x, y)] = (cover, colour, light, level)
    base = [bytearray(bytes(round(c * 255) for c in outline) + b"\x00") * w for _ in range(h)]
    glow_rows = [bytearray(b"\xff\xff\xff\x00" * w) for _ in range(h)]
    for (x, y), (cover, colour, light, level) in cells.items():
        o = (x - x0) * 4
        base[y - y0][o:o + 4] = bytes([round(c / cover * 255) for c in colour] + [round(cover / n * 255)])
        if light:
            glow_rows[y - y0][o:o + 4] = bytes([round(level / light * 255)] * 3 + [round(light / n * 255)])
    return base, glow_rows


def write_layout(regions):
    lines = ['<?xml version="1.0" encoding="utf-8"?>',
             "<Defs>"]
    for gender in GENDERS:
        (lx0, ly0, lx1, ly1), (rx0, ry0, _, _), (sx0, sy0, sx1, sy1) = (regions[(gender, v)] for v, _ in VIEWS)
        lines += ["  <BionicIrises.BionicIrisLayoutDef>",
                  f"    <defName>BionicIrisLayout_{gender}</defName>",
                  f"    <gender>{gender}</gender>",
                  f"    <left>({lx0},{ly0})</left>",
                  f"    <right>({rx0},{ry0})</right>",
                  f"    <frontSize>({lx1 - lx0},{ly1 - ly0})</frontSize>",
                  f"    <side>({sx0},{sy0})</side>",
                  f"    <sideSize>({sx1 - sx0},{sy1 - sy0})</sideSize>",
                  "  </BionicIrises.BionicIrisLayoutDef>"]
    lines.append("</Defs>")
    with open(LAYOUT, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines) + "\n")


def blocks(n):
    return n + -n % 4


def build(root):
    eyes, regions = {}, {}
    for gender in GENDERS:
        folder = os.path.join(root, *NORMAL_EYE, gender)
        south = read_png(os.path.join(folder, "normal_south.png"))
        east = read_png(os.path.join(folder, "normal_east.png"))
        eyes[gender] = {"L": Eye(south, "L"), "R": Eye(south, "R"), "side": Eye(east, None)}
        for view, _ in VIEWS:
            x0, y0, x1, y1 = eyes[gender][view].box
            regions[(gender, view)] = (x0 - PAD, y0 - PAD, x1 + PAD, y1 + PAD)
        x0, y0, x1, y1 = regions[(gender, "side")]
        regions[(gender, "side")] = (x0, y0, x0 + blocks(x1 - x0), y0 + blocks(y1 - y0))
        (lx0, ly0, lx1, ly1), (rx0, ry0, rx1, ry1) = regions[(gender, "L")], regions[(gender, "R")]
        w, h, top = blocks(max(lx1 - lx0, rx1 - rx0)), blocks(max(ly1, ry1) - min(ly0, ry0)), min(ly0, ry0)
        regions[(gender, "L")] = (lx1 - w, top, lx1, top + h)
        regions[(gender, "R")] = (rx0, top, rx0 + w, top + h)
    count = 0
    for name in DESIGNS:
        for gender in GENDERS:
            d = os.path.join(OUT, name, gender)
            os.makedirs(d, exist_ok=True)
            for view, rot in VIEWS:
                base, glow = draw(name, eyes[gender][view], regions[(gender, view)])
                for side in (("L", "R") if view == "side" else (view,)):
                    save_png(os.path.join(d, f"normal_{side}_{rot}.png"), base)
                    save_png(os.path.join(d, f"normal_{side}_glow_{rot}.png"), glow)
                    count += 2
    for rot in ("south", "east"):
        save_png(os.path.join(OUT, f"Mask_{rot}.png"), [bytearray(b"\xff" * 16) for _ in range(4)])
    write_layout(regions)
    print(f"wrote {count} textures and the mask to {os.path.normpath(OUT)}, and the layout")


if __name__ == "__main__":
    build(sys.argv[1] if len(sys.argv) > 1 else EXPERIMENTALS)

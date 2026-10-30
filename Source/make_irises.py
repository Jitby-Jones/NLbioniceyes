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
LIDS = ("Textures", "Things", "Pawn", "FA-Experimentals", "Lids")
SCLERA = 0.1
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
SOCKET_DEEP = hexc("#0c0606")
SOCKET_SHADE = hexc("#2a1616")
SOCKET_LIT = hexc("#a4524a")
SOCKET_EDGE = hexc("#efe9e6")
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


def speckle(u, v, cells, seed):
    n = math.sin(math.floor((u + 1) * cells) * 127.1 + math.floor((v + 1) * cells) * 311.7 + seed * 74.7) * 43758.5453
    return n - math.floor(n)


def artificial(u, v, r, a, su, sv, glints=True):
    grain = math.sin(sv * 19 + math.sin(su * 5 + sv * 2) * 1.8) > 0.6
    if r > 0.66:
        return (lit("#3b2413", "#5c3b20", sv) if grain else lit("#5e3b20", "#93653b", sv)), None
    if r > 0.59:
        return hexc("#2a190d"), None
    if r < 0.25:
        return hexc("#21140b"), None
    if r < 0.31:
        return lit("#73492a", "#ab7a46", sv), None
    if math.cos(a * 10) > 0.5:
        return lit("#45291a", "#6a4528", sv), None
    return lit("#6b4528", "#a2733f", sv), None


def mechanical(u, v, r, a, su, sv, glints=True):
    if glints and glint(su, sv, 0.32, -0.36, 0.11):
        return hexc("#d8d4cb"), GLINT
    if r > 0.72:
        if r > 0.75 and any(abs(math.remainder(a - k * math.pi / 3 - 0.4, 2 * math.pi)) < 0.2 for k in range(6)):
            return lit("#4f3418", "#a06d36", sv), None
        return (lit("#1f1d1b", "#3e3a36", sv) if speckle(u, v, 9, 1) < 0.3 else lit("#2f2c29", "#6c665e", sv)), None
    if r > 0.56 and math.cos(a * 8 + 0.3 * math.sin(a * 3)) <= -0.05:
        return hexc("#121110"), None
    if r < 0.09:
        return lit("#3a3632", "#7d766d", sv), None
    if r < 0.28:
        return hexc("#0d0c0b"), None
    if r < 0.32:
        return hexc("#1a1816"), None
    if speckle(u, v, 10, 2) < 0.22:
        return lit("#22324a", "#3f5670", sv), None
    return lit("#2e4057", "#5f7690", sv), None


SPARKLES = [(-0.36, 0.20, 1.0), (0.35, 0.30, 0.8)]
STARS = [(0.44, 0.04), (0.10, 0.45), (-0.18, -0.42), (-0.46, -0.12), (0.04, -0.46), (-0.26, 0.42), (0.30, -0.26), (-0.44, 0.04)]


def starlight(u, v, r, a):
    level = 0.07 + 0.07 * (math.sin(a * 2 - r * 9) + 1) / 2
    level += max(0.0, 1 - abs(r - 0.285) / 0.05) * 0.85
    level += max(0.0, 1 - abs(r - 0.55) / 0.04) * 0.25
    for x, y in STARS:
        d = math.hypot(u - x, v - y)
        level += 1.0 if d < 0.055 else max(0.0, 1 - (d - 0.055) / 0.05) * 0.6
    for x, y, strength in SPARKLES:
        dx, dy = abs(u - x), abs(v - y)
        d = math.hypot(dx, dy)
        level += strength * (1.0 if d < 0.075 else max(0.0, 1 - (d - 0.075) / 0.06) * 0.7)
        level += strength * max(0.0, 1 - dx / 0.36) * max(0.0, 1 - dy / 0.05)
        level += strength * max(0.0, 1 - dy / 0.36) * max(0.0, 1 - dx / 0.05)
    return min(1.0, level)


def paresian(u, v, r, a, su, sv, glints=True):
    if glints and (glint(su, sv, 0.28, -0.40, 0.08)
                   or (0.44 < math.hypot(su, sv) < 0.52 and -1.5 < math.atan2(sv, su) < -0.8)):
        return WHITE, GLINT
    if r > 0.70:
        return mix(lit("#8b6a2c", "#f0cf70", sv), hexc("#fff0bd"), 0.55 * max(0.0, math.cos(math.atan2(sv, su) + 0.85)) ** 6), None
    if r > 0.67:
        return hexc("#3a2a10"), None
    if r > 0.57:
        if r > 0.59 and min(abs(math.remainder(a - k * math.pi / 2, 2 * math.pi)) for k in range(4)) < 0.24:
            return lit("#0d6a3a", "#45d988", sv), None
        if r > 0.60 and min(abs(math.remainder(a - k * math.pi / 6, 2 * math.pi)) for k in range(12)) < 0.075:
            return hexc("#4b4f55"), None
        return lit("#a7adb4", "#eef2f6", sv), None
    if r < 0.25:
        return hexc("#07060b"), None
    return hexc("#0b0e1f"), starlight(u, v, r, a)


def bolt(u, v, angle):
    return math.hypot(u - 0.78 * math.cos(angle), v - 0.78 * math.sin(angle)) < 0.09


def receptor(u, v, r, a, su, sv, glints=True):
    if glints and glint(su, sv, 0.30, -0.36, 0.12):
        return WHITE, GLINT
    if r > 0.70:
        if bolt(u, v, -2.3) or bolt(u, v, 0.8) or (0.73 < r < 0.82 and 2.55 < a < 3.35):
            return hexc("#1c2124"), None
        return lit("#475154", "#7f898c", sv), None
    if r > 0.63:
        return hexc("#1f2528"), None
    if r > 0.55:
        return lit("#868c8f", "#b3b8ba", sv), None
    if r < 0.09:
        return hexc("#5d6366"), None
    if 0.30 < r < 0.35:
        return mix(lit("#a3a8ab", "#d6d9da", sv), hexc("#7c8285"), 0.5), None
    return lit("#a3a8ab", "#d6d9da", sv), None


def royal(u, v, r, a, su, sv, glints=True):
    if glints and glint(su, sv, 0.30, -0.40, 0.11):
        return WHITE, GLINT
    if r > 0.70:
        sheen = max(0.0, math.cos(math.atan2(sv, su) + 2.4)) ** 4
        return LAMP_OFF, min(1.0, 0.62 + 0.2 * (sv + 1) / 2 + 0.3 * sheen)
    if r > 0.635:
        return lit("#a7782a", "#efc35a", sv), None
    if abs(u) ** 0.7 + abs(v) ** 0.7 < 0.30 ** 0.7:
        return lit("#c99a38", "#f8dc7a", sv), None
    return lit("#060608", "#17171c", sv), None


def blind(u, v, r, a, su, sv, glints=True):
    teeth = 0.5 + 0.5 * math.cos(a * 12 + 0.5)
    split = 0.18 - 0.22 * teeth * smooth(r, 0.25, 0.75)
    col = mix(lit("#4a5561", "#6f7c8a", sv), lit("#a9b5c0", "#eaf0f4", sv), smooth(sv, split - 0.25, split + 0.25))
    fibre = math.cos(a * 26 + 2.0 * math.sin(a * 7 + r * 6))
    reach = smooth(r, 0.2, 0.8)
    col = mix(col, hexc("#323b45"), 0.16 * max(0.0, fibre) * reach)
    col = mix(col, hexc("#ffffff"), 0.8 * 0.16 * max(0.0, -fibre) * reach)
    col = mix(col, hexc("#36404a"), 0.5 * smooth(r, 0.66, 0.84))
    col = mix(col, hexc("#4f5a66"), 0.5 * (1 - smooth(math.hypot(u, v + 0.08), 0.14, 0.27)))
    haze = (math.sin(u * 6.3 + 1.1 + math.sin(v * 4.1) * 1.4) + math.sin(v * 5.2 - u * 2.7 + 0.6) + 2) / 4
    film = 0.12 + 0.30 * (1 - smooth(math.hypot(u + 0.04, v + 0.02), 0.05, 0.55))
    col = mix(col, hexc("#e4eaef"), film * (0.55 + 0.45 * haze))
    col = mix(col, hexc("#ffffff"), 0.45 * (1 - smooth(math.hypot(su - 0.30, sv + 0.36), 0.03, 0.22)))
    return col, None


DESIGNS = {"Bionic": (bionic, OUTLINE), "AdvancedBionic": (advanced, OUTLINE),
           "TacticalBionic": (tactical, OUTLINE), "Archotech": (archotech, hexc("#1d160b")),
           "Artificial": (artificial, hexc("#1c120b")), "Mechanical": (mechanical, OUTLINE),
           "Paresian": (paresian, hexc("#1d160b")), "Blind": (blind, hexc("#252b31")),
           "LightReceptor": (receptor, OUTLINE), "RoyalBionic": (royal, OUTLINE)}


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


def box(points):
    return (min(x for x, _ in points), min(y for _, y in points), max(x for x, _ in points) + 1, max(y for _, y in points) + 1)


def sclera(root, gender, rot):
    whites, bare, opening = {}, {}, {}
    folder = os.path.join(root, *LIDS)
    for lid in sorted(os.listdir(folder)):
        cover = os.path.join(folder, lid, gender, f"normal_cover_{rot}.png")
        bottom = os.path.join(folder, lid, gender, f"normal_bottom_{rot}.png")
        if not (os.path.exists(cover) and os.path.exists(bottom)):
            continue
        shut, white = read_png(cover)[0], read_png(bottom)[0]
        for side in (("L", "R") if rot == "south" else ("side",)):
            for y in range(200, 360):
                for x in range(512):
                    if not (side == "side" or (x < 256) == (side == "L")):
                        continue
                    if white[y][x] > SCLERA:
                        seen = whites.setdefault(side, {})
                        seen[(x, y)] = max(seen.get((x, y), 0.0), white[y][x])
                        if lid == "Normal" and shut[y][x] < 0.5:
                            opening.setdefault(side, []).append((x, y))
                    elif shut[y][x] < 0.5:
                        bare.setdefault(side, set()).add((x, y))
    return whites, bare, {side: box(points) for side, points in opening.items()}


def smooth(v, lo, hi):
    t = min(1.0, max(0.0, (v - lo) / (hi - lo)))
    return t * t * (3 - 2 * t)


def grow(points):
    return {(x + i, y + j) for x, y in points for j in (-1, 0, 1) for i in (-1, 0, 1)}


def socket(region, white, bare, opening):
    x0, y0, x1, y1 = region
    ox0, oy0, ox1, oy1 = opening
    cx, cy, hx, hy = (ox0 + ox1) / 2, (oy0 + oy1) / 2, (ox1 - ox0) / 2, (oy1 - oy0) / 2
    solid = grow(white)
    rows = []
    for y in range(y0, y1):
        row = bytearray()
        for x in range(x0, x1):
            near = min([math.hypot(x - bx, y - by) for by in range(y - 3, y + 4) for bx in range(x - 3, x + 4) if (bx, by) in bare] + [4.0])
            fade = smooth(white.get((x, y), 0.0), SCLERA, 0.9) * smooth(near, 0.5, 3.0)
            total = [0.0, 0.0, 0.0]
            for sy in range(SS):
                for sx in range(SS):
                    dx, dy = (x + (sx + 0.5) / SS - cx) / hx, (y + (sy + 0.5) / SS - cy) / hy
                    d = min(1.0, math.hypot(dx, dy))
                    wall = mix(SOCKET_SHADE, SOCKET_LIT, 0.85 * smooth(dy, 0.1, 0.8))
                    hollow = mix(wall, SOCKET_DEEP, max(0.7 * smooth(-dy, 0.0, 0.8), 0.6 * (1 - d) ** 2))
                    total = [t + c for t, c in zip(total, mix(SOCKET_EDGE, hollow, fade))]
            row += bytes(round(t / (SS * SS) * 255) for t in total) + (b"\xff" if (x, y) in solid else b"\x00")
        rows.append(row)
    return rows


def write_layout(regions, sockets):
    lines = ['<?xml version="1.0" encoding="utf-8"?>',
             "<Defs>"]
    for gender in GENDERS:
        (lx0, ly0, lx1, ly1), (rx0, ry0, _, _), (sx0, sy0, sx1, sy1) = (regions[(gender, v)] for v, _ in VIEWS)
        (kx0, ky0, kx1, ky1), (jx0, jy0, _, _), (tx0, ty0, tx1, ty1) = (sockets[(gender, v)] for v, _ in VIEWS)
        lines += ["  <BionicIrises.BionicIrisLayoutDef>",
                  f"    <defName>BionicIrisLayout_{gender}</defName>",
                  f"    <gender>{gender}</gender>",
                  f"    <left>({lx0},{ly0})</left>",
                  f"    <right>({rx0},{ry0})</right>",
                  f"    <frontSize>({lx1 - lx0},{ly1 - ly0})</frontSize>",
                  f"    <side>({sx0},{sy0})</side>",
                  f"    <sideSize>({sx1 - sx0},{sy1 - sy0})</sideSize>",
                  f"    <leftSocket>({kx0},{ky0})</leftSocket>",
                  f"    <rightSocket>({jx0},{jy0})</rightSocket>",
                  f"    <socketSize>({kx1 - kx0},{ky1 - ky0})</socketSize>",
                  f"    <sideSocket>({tx0},{ty0})</sideSocket>",
                  f"    <sideSocketSize>({tx1 - tx0},{ty1 - ty0})</sideSocketSize>",
                  "  </BionicIrises.BionicIrisLayoutDef>"]
    lines.append("</Defs>")
    with open(LAYOUT, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines) + "\n")


def blocks(n):
    return n + -n % 4


def pair(left, right):
    (lx0, ly0, lx1, ly1), (rx0, ry0, rx1, ry1) = left, right
    w, h, top = blocks(max(lx1 - lx0, rx1 - rx0)), blocks(max(ly1, ry1) - min(ly0, ry0)), min(ly0, ry0)
    return (lx1 - w, top, lx1, top + h), (rx0, top, rx0 + w, top + h)


def build(root):
    eyes, regions, sockets, whites, skins, shapes = {}, {}, {}, {}, {}, {}
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
        regions[(gender, "L")], regions[(gender, "R")] = pair(regions[(gender, "L")], regions[(gender, "R")])
        for rot in ("south", "east"):
            found, bare, opening = sclera(root, gender, rot)
            for view, white in found.items():
                x0, y0, x1, y1 = box(grow(white))
                sockets[(gender, view)] = (x0 - PAD, y0 - PAD, x1 + PAD, y1 + PAD)
                whites[(gender, view)] = white
                skins[(gender, view)] = bare.get(view, set())
                shapes[(gender, view)] = opening[view]
        x0, y0, x1, y1 = sockets[(gender, "side")]
        sockets[(gender, "side")] = (x0, y0, x0 + blocks(x1 - x0), y0 + blocks(y1 - y0))
        sockets[(gender, "L")], sockets[(gender, "R")] = pair(sockets[(gender, "L")], sockets[(gender, "R")])
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
    for gender in GENDERS:
        d = os.path.join(OUT, "Eyeless", gender)
        os.makedirs(d, exist_ok=True)
        for view, rot in VIEWS:
            x0, y0, x1, y1 = sockets[(gender, view)]
            base = socket(sockets[(gender, view)], whites[(gender, view)], skins[(gender, view)], shapes[(gender, view)])
            glow = [bytearray(b"\xff\xff\xff\x00" * (x1 - x0)) for _ in range(y1 - y0)]
            for side in (("L", "R") if view == "side" else (view,)):
                save_png(os.path.join(d, f"normal_{side}_{rot}.png"), base)
                save_png(os.path.join(d, f"normal_{side}_glow_{rot}.png"), glow)
                count += 2
    for rot in ("south", "east"):
        save_png(os.path.join(OUT, f"Mask_{rot}.png"), [bytearray(b"\xff" * 16) for _ in range(4)])
    write_layout(regions, sockets)
    print(f"wrote {count} textures and the mask to {os.path.normpath(OUT)}, and the layout")


if __name__ == "__main__":
    build(sys.argv[1] if len(sys.argv) > 1 else EXPERIMENTALS)

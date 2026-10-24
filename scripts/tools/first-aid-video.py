#!/usr/bin/env python3
"""
Example film for the Kiosk messages (config/news/pierwsza-pomoc.mp4): an illustrated first-aid guide
(resuscitation with an AED, following the ERC basic life support steps), drawn frame by frame and encoded by ffmpeg.

    python3 scripts/tools/first-aid-video.py config/news/pierwsza-pomoc.mp4

Needs Pillow, ffmpeg (libx264) and the Inter font (any TrueType font works: change FONT_*).
"""
import math
import subprocess
import sys

from PIL import Image, ImageDraw, ImageFont

W, H, FPS, SS = 1280, 720, 25, 2   # drawn at twice the size, then scaled down (smooth edges)
FONT_BOLD = "/usr/share/fonts/opentype/inter/Inter-Bold.otf"
FONT_MEDIUM = "/usr/share/fonts/opentype/inter/Inter-Medium.otf"

BACK = (13, 20, 35)
PANEL = (22, 34, 58)
FLOOR = (34, 50, 80)
TEXT = (240, 245, 252)
MUTED = (175, 190, 210)
ACCENT = (90, 181, 255)
RED = (220, 38, 38)
GREEN = (22, 163, 74)
ORANGE = (249, 115, 22)
SKIN = (241, 194, 155)
SKIN_DARK = (222, 168, 128)
HAIR = (74, 52, 40)
SHIRT = (59, 130, 246)
PANTS = (30, 58, 138)
RESCUER = (20, 184, 166)
RESCUER_PANTS = (51, 65, 85)
SHOE = (17, 24, 39)

_fonts = {}


def font(size, bold=True):
    key = (size, bold)
    if key not in _fonts:
        _fonts[key] = ImageFont.truetype(FONT_BOLD if bold else FONT_MEDIUM, size * SS)
    return _fonts[key]


def ease(x):
    x = max(0.0, min(1.0, x))
    return x * x * (3 - 2 * x)


def ramp(t, start, length):
    return ease((t - start) / length)


class Canvas:
    def __init__(self):
        self.img = Image.new("RGB", (W * SS, H * SS), BACK)
        self.d = ImageDraw.Draw(self.img)

    @staticmethod
    def p(*xy):
        return [v * SS for v in xy]

    def circle(self, x, y, r, fill, outline=None, width=0):
        self.d.ellipse(self.p(x - r, y - r, x + r, y + r), fill=fill, outline=outline, width=int(width * SS))

    def ellipse(self, x0, y0, x1, y1, fill):
        self.d.ellipse(self.p(x0, y0, x1, y1), fill=fill)

    def rrect(self, x0, y0, x1, y1, r, fill, outline=None, width=0):
        self.d.rounded_rectangle(self.p(min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1)), radius=r * SS, fill=fill,
                                 outline=outline, width=int(width * SS))

    def poly(self, points, fill):
        self.d.polygon([(x * SS, y * SS) for x, y in points], fill=fill)

    def line(self, points, width, fill, round_ends=True):
        pts = [(x * SS, y * SS) for x, y in points]
        self.d.line(pts, fill=fill, width=int(width * SS), joint="curve")
        if round_ends:
            for x, y in (points[0], points[-1]):
                self.circle(x, y, width / 2, fill)

    def arc(self, x, y, r, start, end, fill, width):
        self.d.arc(self.p(x - r, y - r, x + r, y + r), start, end, fill=fill, width=int(width * SS))

    def text(self, x, y, s, size, fill=TEXT, bold=True, anchor="la"):
        self.d.text((x * SS, y * SS), s, font=font(size, bold), fill=fill, anchor=anchor)

    def text_center(self, y, s, size, fill=TEXT, bold=True):
        self.d.multiline_text((W / 2 * SS, y * SS), s, font=font(size, bold), fill=fill, anchor="ma", align="center",
                              spacing=10 * SS)

    def frame(self):
        return self.img.resize((W, H), Image.LANCZOS)


def rotate(points, pivot, angle):
    c, s = math.cos(angle), math.sin(angle)
    px, py = pivot
    return [(px + (x - px) * c - (y - py) * s, py + (x - px) * s + (y - py) * c) for x, y in points]


# ------------------------------------------------------------------ pieces


PANEL_BOX = (60, 120, W - 60, 575)


def layout(c, number, title, caption, color=ACCENT):
    """The scene draws its picture on the panel (clipped to it later); the title and caption go around it."""
    c.header = (number, title, caption, color)
    c.d.rectangle(c.p(0, 0, W, H), fill=PANEL)


def chrome(c, number, title, caption, color):
    if number:
        c.circle(100, 68, 30, color)
        c.text(100, 68, str(number), 30, anchor="mm")
        c.text(148, 68, title, 38, anchor="lm")
    else:
        c.text(W / 2, 68, title, 40, anchor="mm")
    c.text_center(600, caption, 26, MUTED, bold=False)


def heart(c, x, y, size, fill):
    pts = []
    for i in range(80):
        a = 2 * math.pi * i / 80
        hx = 16 * math.sin(a) ** 3
        hy = 13 * math.cos(a) - 5 * math.cos(2 * a) - 2 * math.cos(3 * a) - math.cos(4 * a)
        pts.append((x + hx * size / 16, y - hy * size / 16))
    c.poly(pts, fill)


def bolt(c, x, y, size, fill):
    s = size / 10
    c.poly([(x + 1 * s, y - 10 * s), (x - 5 * s, y + 1 * s), (x - 0.5 * s, y + 1 * s), (x - 2 * s, y + 10 * s),
            (x + 5 * s, y - 2 * s), (x + 0.5 * s, y - 2 * s), (x + 3 * s, y - 10 * s)], fill)


def aed_box(c, x, y, scale=1.0):
    w, h = 150 * scale, 120 * scale
    c.rrect(x - w / 2, y - h / 2, x + w / 2, y + h / 2, 18 * scale, GREEN)
    c.rrect(x - w / 2 + 10 * scale, y - h / 2 - 16 * scale, x + w / 2 - 10 * scale, y - h / 2 + 4 * scale, 8 * scale, (16, 120, 56))
    heart(c, x - 22 * scale, y + 2 * scale, 30 * scale, (255, 255, 255))
    bolt(c, x - 22 * scale, y, 16 * scale, GREEN)
    c.text(x + 30 * scale, y + 2 * scale, "AED", int(26 * scale), anchor="mm")


def floor(c, ground):
    c.rrect(100, ground - 6, W - 100, ground + 14, 10, FLOOR)


def victim(c, x0, ground, dip=0.0):
    """Lying on the back, head on the left."""
    floor(c, ground)
    # Legs and shoes.
    c.rrect(x0 + 300, ground - 70, x0 + 590, ground - 22, 22, PANTS)
    c.rrect(x0 + 580, ground - 96, x0 + 616, ground - 20, 14, SHOE)
    # Torso: the chest gives way under the hands.
    top = []
    for i in range(41):
        x = x0 + 40 + i * 7
        bump = math.exp(-((x - (x0 + 170)) / 45) ** 2)
        top.append((x, ground - 82 + dip * bump))
    c.poly(top + [(x0 + 320, ground - 74), (x0 + 320, ground - 20), (x0 + 40, ground - 20)], SHIRT)
    # Arm along the body.
    c.rrect(x0 + 70, ground - 44, x0 + 290, ground - 20, 12, (37, 99, 235))
    c.circle(x0 + 296, ground - 32, 14, SKIN)
    # Head and neck.
    c.rrect(x0 + 10, ground - 70, x0 + 52, ground - 30, 10, SKIN_DARK)
    c.circle(x0 - 10, ground - 52, 38, SKIN)
    c.d.pieslice(c.p(x0 - 48, ground - 90, x0 + 28, ground - 14), 100, 260, fill=HAIR)
    c.poly([(x0 - 14, ground - 88), (x0 - 4, ground - 104), (x0 + 6, ground - 88)], SKIN)   # nose
    c.circle(x0 - 10, ground - 52, 8, SKIN_DARK)   # ear


def rescuer_kneeling(c, chest, ground, behind=True, lean=0.0):
    """Kneeling on the far side; returns the shoulder point."""
    cx, cy = chest
    shoulder = (cx + 8 + lean, cy - 150)
    hip = (cx + 120, ground - 70)
    if behind:
        c.line([hip, (hip[0] - 10, ground - 52)], 50, RESCUER_PANTS)
        c.line([hip, (hip[0] + 40, ground - 52)], 46, RESCUER_PANTS)
        c.line([shoulder, hip], 72, RESCUER)
        c.circle(shoulder[0] + 30, shoulder[1] - 58, 34, SKIN)
        c.d.pieslice(c.p(shoulder[0] - 4, shoulder[1] - 94, shoulder[0] + 64, shoulder[1] - 26), 180, 360, fill=HAIR)
        c.rrect(shoulder[0] + 14, shoulder[1] - 34, shoulder[0] + 42, shoulder[1] - 8, 8, SKIN_DARK)
    return shoulder


def arms_to(c, shoulder, hand, sleeve=RESCUER):
    mid = (shoulder[0] + (hand[0] - shoulder[0]) * 0.55, shoulder[1] + (hand[1] - shoulder[1]) * 0.55)
    c.line([shoulder, mid], 30, sleeve)
    c.line([mid, hand], 24, SKIN)


def standing_person(c, x, ground, look=0.0):
    c.line([(x - 18, ground - 120), (x - 24, ground - 4)], 30, RESCUER_PANTS)
    c.line([(x + 18, ground - 120), (x + 24, ground - 4)], 30, RESCUER_PANTS)
    c.rrect(x - 42, ground - 270, x + 42, ground - 110, 34, RESCUER)
    c.line([(x - 40, ground - 250), (x - 66, ground - 150)], 24, RESCUER)
    c.line([(x + 40, ground - 250), (x + 66, ground - 150)], 24, RESCUER)
    hx = x + look * 8
    c.circle(hx, ground - 310, 36, SKIN)
    c.d.pieslice(c.p(hx - 38, ground - 350, hx + 38, ground - 280), 180, 360, fill=HAIR)
    c.circle(hx - 12 + look * 10, ground - 312, 4, SHOE)
    c.circle(hx + 12 + look * 10, ground - 312, 4, SHOE)


def bubble(c, x, y, s, size=24):
    tw = c.d.textlength(s, font=font(size)) / SS
    c.rrect(x - 20, y - 30, x + tw + 20, y + 30, 22, (255, 255, 255))
    c.poly([(x + 10, y + 26), (x - 6, y + 50), (x + 34, y + 26)], (255, 255, 255))
    c.text(x, y, s, size, fill=BACK, anchor="lm")


def head_close(c, pivot, tilt, hands=True):
    """Close-up of the head lying face up (profile); tilt < 0 bends it back, chin up. Returns the mouth."""
    px, py = pivot

    def at(points):
        return rotate([(px + x, py + y) for x, y in points], pivot, tilt)

    # Shoulders and neck (they stay).
    c.rrect(px - 20, py - 100, px + 480, py + 60, 50, SHIRT)
    c.poly([(px - 70, py - 90), (px + 10, py - 96), (px + 10, py - 4), (px - 90, py - 4)], SKIN_DARK)
    skull = at([(-175 + 100 * math.cos(a), -55 + 100 * math.sin(a)) for a in [i * math.pi / 40 for i in range(80)]])
    c.poly(skull, SKIN)
    face = [(-262, -95), (-246, -138), (-218, -158), (-196, -150), (-184, -160), (-160, -204), (-140, -168), (-124, -166),
            (-114, -174), (-104, -163), (-96, -170), (-84, -162), (-64, -154), (-44, -128), (-30, -98), (-10, -86),
            (-10, -20), (-120, -10), (-200, -30)]
    c.poly(at(face), SKIN)
    hair = at([(-175 + 104 * math.cos(a), -55 + 104 * math.sin(a)) for a in [math.radians(80 + i * 4) for i in range(36)]] + [(-175, -55)])
    c.poly(hair, HAIR)
    ear = at([(-150 + 20 * math.cos(a), -50 + 28 * math.sin(a)) for a in [i * math.pi / 12 for i in range(24)]])
    c.poly(ear, SKIN_DARK)
    eye = at([(-214, -140), (-192, -138)])
    c.line(eye, 5, SHOE)
    mouth = at([(-104, -160)])[0]
    if hands:
        forehead = at([(-232, -158)])[0]
        chin = at([(-56, -146)])[0]
        arms_to(c, (forehead[0] - 120, forehead[1] - 300), forehead)
        c.ellipse(forehead[0] - 50, forehead[1] - 22, forehead[0] + 44, forehead[1] + 14, SKIN_DARK)
        arms_to(c, (chin[0] + 150, chin[1] - 300), chin)
        c.ellipse(chin[0] - 14, chin[1] - 10, chin[0] + 30, chin[1] + 22, SKIN_DARK)
    return mouth


def ring(c, x, y, r, progress, color, width=10):
    c.arc(x, y, r, 0, 360, (45, 60, 90), width)
    if progress > 0:
        c.arc(x, y, r, -90, -90 + 360 * progress, color, width)


# ------------------------------------------------------------------ scenes (t: seconds since the scene started)


def intro(c, t):
    c.header = (0, "Pierwsza pomoc", "Resuscytacja krok po kroku, z użyciem defibrylatora AED", ACCENT)
    c.d.rectangle(c.p(0, 0, W, H), fill=PANEL)
    beat = 1 + 0.06 * max(0, math.sin(t * 2 * math.pi * 1.2)) ** 4
    heart(c, W / 2, 300, 110 * beat, RED)
    c.rrect(W / 2 - 14, 255, W / 2 + 14, 345, 6, (255, 255, 255))
    c.rrect(W / 2 - 45, 286, W / 2 + 45, 314, 6, (255, 255, 255))
    # ECG line drawing itself.
    pts, n = [], int(min(1, t / 2.5) * 120)
    for i in range(n):
        x = 160 + i * 8
        k = (i % 40)
        y = 470 - (60 if k == 20 else -30 if k == 22 else 15 if k == 17 else 0)
        pts.append((x, y))
    if len(pts) > 1:
        c.line(pts, 6, ACCENT)


def safety(c, t):
    layout(c, 1, "Oceń bezpieczeństwo", "Rozejrzyj się: ruch uliczny, prąd, ogień, gaz.\nPodejdź, gdy tobie i poszkodowanemu nic nie grozi.")
    victim(c, 300, 520)
    standing_person(c, 1000, 540, look=math.sin(t * 2.2))
    safe = t > 3
    x, y = 1060, 200
    if not safe:
        c.poly([(x, y - 46), (x - 52, y + 40), (x + 52, y + 40)], (250, 204, 21))
        c.text(x, y + 8, "!", 44, fill=BACK, anchor="mm")
    else:
        c.circle(x, y, 46, GREEN)
        c.line([(x - 20, y), (x - 4, y + 16), (x + 22, y - 14)], 10, (255, 255, 255))


def consciousness(c, t):
    layout(c, 2, "Sprawdź reakcję", "Delikatnie potrząśnij za ramiona i głośno zapytaj:\n„Czy wszystko w porządku?”")
    ground = 520
    shake = 5 * math.sin(t * 2 * math.pi * 3) if 0.5 < t % 2.5 < 2.0 else 0
    shoulder = rescuer_kneeling(c, (380 + shake, ground - 80), ground)
    victim(c, 300, ground)
    arms_to(c, shoulder, (350 + shake, ground - 84))
    arms_to(c, (shoulder[0] + 20, shoulder[1]), (400 + shake, ground - 84))
    if t > 0.6:
        bubble(c, 520, 200, "Czy wszystko w porządku?")


def airway(c, t):
    layout(c, 3, "Udrożnij drogi oddechowe i oceń oddech",
           "Dłoń na czole, palce pod brodą: odegnij głowę do tyłu.\nPatrz, słuchaj i wyczuj oddech, nie dłużej niż 10 sekund.")
    tilt = -0.32 * ramp(t, 0.6, 1.6)
    mouth = head_close(c, (640, 470), tilt)
    if t > 2.6:
        k = (t - 2.6)
        for i in range(3):
            r = 30 + ((k * 40 + i * 25) % 75)
            c.arc(mouth[0], mouth[1], r, 200, 340, (*ACCENT,), 5)
        ring(c, 1040, 330, 70, min(1, k / 4.2), ACCENT)
        c.text(1040, 330, str(max(1, 10 - int(k / 4.2 * 10))) + " s", 34, anchor="mm")
        c.text(1040, 430, "patrz · słuchaj · wyczuj", 22, MUTED, bold=False, anchor="mm")


def call(c, t):
    layout(c, 4, "Wezwij pomoc: 112", "Nie oddycha prawidłowo? Dzwoń pod 112, włącz głośnik.\nPoproś kogoś o przyniesienie defibrylatora AED.", RED)
    x, y = 400, 350
    c.rrect(x - 95, y - 185, x + 95, y + 185, 30, (30, 41, 59))
    c.rrect(x - 80, y - 160, x + 80, y + 150, 16, (241, 245, 249))
    digits = "112"[:max(0, min(3, int((t - 0.3) / 0.45) + 1))] if t > 0.3 else ""
    c.text(x, y - 100, digits, 60, fill=BACK, anchor="mm")
    if t > 1.8:
        pulse = (t * 1.5) % 1
        c.circle(x, y + 70, 36 + 30 * pulse, None, outline=(34, 197, 94), width=4 * (1 - pulse) + 1)
        c.circle(x, y + 70, 36, (34, 197, 94))
        c.poly([(x - 14, y + 58), (x - 6, y + 52), (x + 2, y + 66), (x - 4, y + 72), (x + 6, y + 84), (x + 14, y + 78),
                (x + 18, y + 88), (x + 4, y + 92), (x - 16, y + 70)], (255, 255, 255))
    slide = ramp(t, 2.6, 1.0)
    if slide > 0:
        ax = 1000 + 200 * (1 - slide)
        c.line([(560, 350), (560 + 230 * slide, 350)], 8, MUTED)
        c.poly([(560 + 230 * slide, 334), (586 + 230 * slide, 350), (560 + 230 * slide, 366)], MUTED)
        aed_box(c, ax, 350, 1.3)
        c.text(ax, 460, "Przynieś AED", 26, anchor="mm")


def compressions(c, t):
    layout(c, 5, "30 uciśnięć klatki piersiowej", "Dłonie na środku klatki, ręce wyprostowane.\nUciskaj na 5–6 cm, 100–120 razy na minutę.")
    ground = 520
    phase = t * 110 / 60
    dip = 16 * (0.5 - 0.5 * math.cos(2 * math.pi * phase))
    chest = (470, ground - 82 + dip)
    shoulder = rescuer_kneeling(c, chest, ground, lean=0)
    shoulder = (shoulder[0], shoulder[1] + dip)
    victim(c, 300, ground, dip)
    arms_to(c, shoulder, chest)
    arms_to(c, (shoulder[0] + 16, shoulder[1]), (chest[0] + 6, chest[1]))
    c.ellipse(chest[0] - 30, chest[1] - 18, chest[0] + 34, chest[1] + 6, SKIN_DARK)
    count = int(phase) % 30 + 1
    c.circle(1040, 250, 78, ACCENT)
    c.text(1040, 240, str(count), 64, fill=BACK, anchor="mm")
    c.text(1040, 295, "z 30", 22, fill=BACK, anchor="mm")
    c.text(1040, 380, "5–6 cm", 30, anchor="mm")
    c.text(1040, 420, "100–120 / min", 26, MUTED, bold=False, anchor="mm")


def breaths(c, t):
    layout(c, 6, "2 oddechy ratownicze", "Odegnij głowę, zatkaj nos i wdmuchnij powietrze przez 1 sekundę.\nKlatka piersiowa ma się unieść. Potem znów 30 uciśnięć.")
    mouth = head_close(c, (640, 470), -0.32)
    for n in range(2):
        start = 0.6 + n * 1.8
        if start < t < start + 1.2:
            k = (t - start) / 1.2
            for i in range(3):
                x, y = mouth[0] - 30 + i * 30, mouth[1] - 150 + 110 * k - i * 18
                c.line([(x, y - 34), (x, y)], 7, ACCENT)
                c.poly([(x - 11, y), (x + 11, y), (x, y + 16)], ACCENT)
    c.circle(1040, 300, 70, ACCENT)
    c.text(1040, 300, "×" + str(1 if t < 2.4 else 2), 54, fill=BACK, anchor="mm")


def defibrillator(c, t):
    if t < 3.5:
        caption = "Włącz AED i słuchaj poleceń.\nPrzyklej elektrody na nagą klatkę piersiową, jak na rysunku."
    elif t < 6.5:
        caption = "Analiza rytmu: nikt nie dotyka poszkodowanego."
    else:
        caption = "Gdy AED każe, naciśnij przycisk wyładowania.\nPotem od razu wróć do uciskania."
    layout(c, 7, "Użyj defibrylatora AED", caption, GREEN)
    cx = 520
    # Seen from above.
    c.rrect(cx - 190, 230, cx - 120, 520, 34, SKIN_DARK)
    c.rrect(cx + 120, 230, cx + 190, 520, 34, SKIN_DARK)
    c.rrect(cx - 120, 210, cx + 120, 540, 60, SKIN)
    c.rrect(cx - 105, 520, cx + 105, 572, 18, PANTS)
    c.rrect(cx - 26, 170, cx + 26, 220, 10, SKIN_DARK)
    c.circle(cx, 150, 52, SKIN)
    c.d.pieslice(c.p(cx - 54, 96, cx + 54, 204), 180, 360, fill=HAIR)
    aed_x, aed_y = 960, 370
    pads = [((cx - 60, 275), ramp(t, 0.6, 0.5)), ((cx + 95, 420), ramp(t, 1.6, 0.5))]
    for (px, py), k in pads:
        if k <= 0:
            continue
        c.line([(px, py), ((px + aed_x) / 2, py + 140), (aed_x - 110, aed_y + 30)], 6, (203, 213, 225), round_ends=False)
        w, h = 46 * k, 62 * k
        c.rrect(px - w, py - h, px + w, py + h, 14, (248, 250, 252))
        if k > 0.8:
            bolt(c, px, py, 22, ORANGE)
    # The AED.
    c.rrect(aed_x - 130, aed_y - 110, aed_x + 130, aed_y + 110, 26, (250, 204, 21))
    c.rrect(aed_x - 105, aed_y - 85, aed_x + 105, aed_y - 5, 12, (15, 23, 42))
    if t > 3.5:
        pts = []
        for i in range(48):
            x = aed_x - 95 + i * 4
            k = (i + int(t * 20)) % 16
            pts.append((x, aed_y - 45 - (22 if k == 8 else -10 if k == 9 else 0)))
        c.line(pts, 3, (74, 222, 128))
    flash = t > 6.5 and int(t * 4) % 2 == 0
    c.circle(aed_x, aed_y + 52, 34, ORANGE if flash or t < 6.5 else (194, 65, 12))
    bolt(c, aed_x, aed_y + 52, 22, (255, 255, 255))
    if 3.5 < t < 6.5:
        pulse = (t * 1.2) % 1
        c.circle(cx, 370, 230 + 20 * pulse, None, outline=RED, width=6)
        c.rrect(aed_x - 150, aed_y + 130, aed_x + 150, aed_y + 182, 20, RED)
        c.text(aed_x, aed_y + 156, "NIE DOTYKAĆ", 26, anchor="mm")
    if t > 6.5 and int(t * 4) % 2 == 0:
        bolt(c, cx, 370, 90, (250, 204, 21))


def finish(c, t):
    layout(c, 0, "Kontynuuj do przyjazdu pomocy", "30 uciśnięć i 2 oddechy na zmianę, AED zostaje włączony.\nPrzykładowy film Kiosku: podmień go na własny instruktaż.")
    spin = t * 1.4
    for i in range(2):
        a0 = math.degrees(spin) + i * 180
        c.arc(W / 2, 340, 150, a0, a0 + 140, ACCENT, 14)
        ang = math.radians(a0 + 140)
        x, y = W / 2 + 150 * math.cos(ang), 340 + 150 * math.sin(ang)
        tx, ty = -math.sin(ang), math.cos(ang)
        c.poly([(x + tx * 26, y + ty * 26), (x - ty * 20, y + tx * 20), (x + ty * 20, y - tx * 20)], ACCENT)
    c.text(W / 2, 340, "30 : 2", 62, anchor="mm")
    c.circle(1040, 250, 60, RED)
    c.text(1040, 250, "112", 34, anchor="mm")
    aed_box(c, 240, 260, 1.0)


SCENES = [(intro, 4.5), (safety, 6), (consciousness, 5.5), (airway, 8), (call, 6), (compressions, 9), (breaths, 5),
          (defibrillator, 10), (finish, 6)]


def compose(scene, t):
    picture = Canvas()
    scene(picture, t)
    c = Canvas()
    mask = Image.new("L", c.img.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([v * SS for v in PANEL_BOX], radius=28 * SS, fill=255)
    c.img.paste(picture.img, (0, 0), mask)
    chrome(c, *picture.header)
    return c


def main(out):
    total = sum(d for _, d in SCENES)
    ff = subprocess.Popen(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24",
                           "-s", f"{W}x{H}", "-r", str(FPS), "-i", "-", "-c:v", "libx264", "-preset", "slow", "-crf", "24",
                           "-pix_fmt", "yuv420p", "-movflags", "+faststart", out], stdin=subprocess.PIPE)
    start = 0.0
    for scene, length in SCENES:
        for f in range(int(length * FPS)):
            t = f / FPS
            c = compose(scene, t)
            # Progress of the whole film, and a short fade at each scene change.
            c.rrect(0, H - 8, W * (start + t) / total, H, 0, ACCENT)
            img = c.frame()
            fade = min(1, t / 0.35, (length - t) / 0.25)
            if fade < 1:
                img = Image.blend(Image.new("RGB", (W, H), BACK), img, max(0, fade))
            ff.stdin.write(img.tobytes())
        start += length
    ff.stdin.close()
    ff.wait()


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "pierwsza-pomoc.mp4")

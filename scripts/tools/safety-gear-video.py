#!/usr/bin/env python3
"""
Example film for the Kiosk messages (config/news/bhp-ochrona.mp4): the required personal protective equipment
(helmet, high-visibility vest, safety boots). A figure without them is crossed out, then the same figure puts them on
and each item is named. Drawn with the pieces of first-aid-video.py and encoded by ffmpeg.

    python3 scripts/tools/safety-gear-video.py config/news/bhp-ochrona.mp4
"""
import importlib.util
import math
import os
import subprocess
import sys

from PIL import Image

_spec = importlib.util.spec_from_file_location("first_aid", os.path.join(os.path.dirname(os.path.abspath(__file__)), "first-aid-video.py"))
fa = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(fa)

W, H, FPS = fa.W, fa.H, fa.FPS
Canvas, ease, ramp = fa.Canvas, fa.ease, fa.ramp
PANEL, TEXT, MUTED, ACCENT, RED, GREEN = fa.PANEL, fa.TEXT, fa.MUTED, fa.ACCENT, fa.RED, fa.GREEN
SKIN, SKIN_DARK, HAIR, SHOE = fa.SKIN, fa.SKIN_DARK, fa.HAIR, fa.SHOE

TSHIRT = (100, 116, 139)
JEANS = (37, 99, 235)
SNEAKER = (226, 232, 240)
HELMET = (250, 204, 21)
HELMET_DARK = (202, 160, 8)
VEST = (249, 115, 22)
REFLECT = (226, 232, 240)
BOOT = (30, 30, 30)
TOE = (250, 204, 21)


def _person(c, x, ground, helmet=0.0, vest=0.0, boots=0.0, wave=0.0):
    """Front view, standing. helmet / vest / boots: 0 none … 1 fully on (they come in from above or fade in)."""
    hip_y, shoulder_y, head_y = ground - 190, ground - 360, ground - 420
    # Legs.
    for side in (-1, 1):
        c.rrect(x + side * 8 - (36 if side < 0 else 0), hip_y, x + side * 8 + (36 if side > 0 else 0), ground - 26, 16, JEANS)
    # Shoes, then boots over them.
    for side in (-1, 1):
        fx = x + side * 30
        c.rrect(fx - 30, ground - 30, fx + 30, ground, 14, SNEAKER)
        c.rrect(fx - 30, ground - 8, fx + 30, ground, 4, (148, 163, 184))
    if boots > 0:
        drop = (1 - ease(boots)) * 60
        for side in (-1, 1):
            fx = x + side * 30
            c.rrect(fx - 34, ground - 70 - drop, fx + 34, ground + 2 - drop, 14, BOOT)
            c.rrect(fx - 34, ground - 26 - drop, fx + 34, ground - 6 - drop, 8, TOE)          # steel toe cap
            c.rrect(fx - 36, ground - 6 - drop, fx + 36, ground + 4 - drop, 4, (70, 70, 70))  # sole
    # Torso and arms (one arm can wave).
    c.rrect(x - 70, shoulder_y, x + 70, hip_y + 20, 34, TSHIRT)
    c.line([(x - 66, shoulder_y + 24), (x - 92, shoulder_y + 150)], 30, TSHIRT)
    c.circle(x - 94, shoulder_y + 172, 17, SKIN)
    angle = -0.4 - 0.5 * wave
    hand = (x + 66 + 150 * math.sin(-angle) * 0.9, shoulder_y + 24 + 150 * math.cos(angle) * (1 - 2 * wave))
    c.line([(x + 66, shoulder_y + 24), hand], 30, TSHIRT)
    c.circle(hand[0] + 2, hand[1] + 18, 17, SKIN)
    # Vest over the t-shirt: two front panels, reflective bands.
    if vest > 0:
        drop = (1 - ease(vest)) * 80
        y0, y1 = shoulder_y - 4 + drop, hip_y + 24 + drop
        c.rrect(x - 74, y0, x - 10, y1, 26, VEST)
        c.rrect(x + 10, y0, x + 74, y1, 26, VEST)
        for y in (y0 + 110, y0 + 150):
            c.rrect(x - 74, y, x - 10, y + 16, 2, REFLECT)
            c.rrect(x + 10, y, x + 74, y + 16, 2, REFLECT)
        c.rrect(x - 58, y0 + 4, x - 42, y0 + 104, 2, REFLECT)
        c.rrect(x + 42, y0 + 4, x + 58, y0 + 104, 2, REFLECT)
    # Neck, head, face.
    c.rrect(x - 16, head_y + 30, x + 16, shoulder_y + 8, 6, SKIN_DARK)
    c.circle(x, head_y, 48, SKIN)
    c.d.pieslice(c.p(x - 50, head_y - 52, x + 50, head_y + 40), 180, 360, fill=HAIR)
    c.circle(x - 17, head_y + 4, 5, SHOE)
    c.circle(x + 17, head_y + 4, 5, SHOE)
    c.arc(x, head_y + 14, 16, 30, 150, (150, 70, 60), 4)
    if helmet > 0:
        drop = (1 - ease(helmet)) * 160
        hy = head_y - 8 - drop
        c.d.pieslice(c.p(x - 58, hy - 60, x + 58, hy + 52), 180, 360, fill=HELMET)
        c.rrect(x - 70, hy - 6, x + 70, hy + 8, 6, HELMET_DARK)            # brim
        c.rrect(x - 7, hy - 58, x + 7, hy - 6, 4, HELMET_DARK)             # ridge
    return {"helmet": (x + 40, head_y - 50), "vest": (x + 60, shoulder_y + 120), "boots": (x + 50, ground - 30)}


SCALE = 0.74


def person(c, x, ground, helmet=0.0, vest=0.0, boots=0.0, wave=0.0):
    """The figure drawn full size on its own layer, then shrunk around its feet so it fits the panel with the helmet."""
    layer = Canvas()
    layer.img = Image.new("RGBA", layer.img.size, (0, 0, 0, 0))
    layer.d = fa.ImageDraw.Draw(layer.img)
    points = _person(layer, x, ground, helmet, vest, boots, wave)
    size = (int(layer.img.width * SCALE), int(layer.img.height * SCALE))
    small = layer.img.resize(size, Image.LANCZOS)
    ox, oy = x * fa.SS * (1 - SCALE), ground * fa.SS * (1 - SCALE)
    c.img.paste(small, (int(ox), int(oy)), small)
    return {k: (x + (px - x) * SCALE, ground + (py - ground) * SCALE) for k, (px, py) in points.items()}


def cross(c, x, y, r, width=14):
    c.line([(x - r, y - r), (x + r, y + r)], width, RED)
    c.line([(x - r, y + r), (x + r, y - r)], width, RED)


def check(c, x, y, r):
    c.circle(x, y, r, GREEN)
    c.line([(x - r * 0.45, y), (x - r * 0.1, y + r * 0.35), (x + r * 0.5, y - r * 0.35)], r * 0.22, (255, 255, 255))


def callout(c, point, label, ok, y, side=1, alpha=1.0):
    """A line from the figure to a label; red with an X when missing, green with a tick when worn."""
    if alpha <= 0:
        return
    color = GREEN if ok else RED
    x0 = point[0] + side * 30
    lx = point[0] + side * 170
    c.line([point, (x0, y), (lx, y)], 4, color)
    c.circle(point[0], point[1], 8, color)
    tw = c.d.textlength(label, font=fa.font(24)) / fa.SS
    box_x0 = lx if side > 0 else lx - tw - 70
    c.rrect(box_x0, y - 26, box_x0 + tw + 70, y + 26, 26, color)
    ix = box_x0 + 26
    if ok:
        c.line([(ix - 9, y), (ix - 2, y + 8), (ix + 10, y - 8)], 5, (255, 255, 255))
    else:
        c.line([(ix - 8, y - 8), (ix + 8, y + 8)], 5, (255, 255, 255))
        c.line([(ix - 8, y + 8), (ix + 8, y - 8)], 5, (255, 255, 255))
    c.text(box_x0 + 48, y, label, 24, anchor="lm")


ITEMS = [("helmet", "Kask ochronny", "Brak kasku"), ("vest", "Kamizelka odblaskowa", "Brak kamizelki"),
         ("boots", "Buty ochronne", "Brak butów ochronnych")]


def intro(c, t):
    c.header = (0, "Bezpieczeństwo na terenie zakładu", "Na halę i plac wchodzisz tylko w środkach ochrony indywidualnej.", ACCENT)
    c.d.rectangle(c.p(0, 0, W, H), fill=PANEL)
    icons = [(380, "Kask"), (640, "Kamizelka"), (900, "Buty")]
    for i, (x, name) in enumerate(icons):
        k = ramp(t, 0.4 + i * 0.5, 0.5)
        if k <= 0:
            continue
        y = 330 + (1 - k) * 40
        c.circle(x, y, 90, (30, 46, 76))
        if i == 0:
            c.d.pieslice(c.p(x - 58, y - 50, x + 58, y + 62), 180, 360, fill=HELMET)
            c.rrect(x - 70, y + 2, x + 70, y + 16, 6, HELMET_DARK)
        elif i == 1:
            c.rrect(x - 50, y - 50, x - 6, y + 50, 16, VEST)
            c.rrect(x + 6, y - 50, x + 50, y + 50, 16, VEST)
            c.rrect(x - 50, y + 10, x + 50, y + 22, 2, REFLECT)
        else:
            for dx in (-28, 28):
                c.rrect(x + dx - 24, y - 40, x + dx + 24, y + 36, 12, BOOT)
                c.rrect(x + dx - 24, y + 14, x + dx + 24, y + 30, 6, TOE)
        c.text(x, y + 130, name, 28, anchor="mm")


def without(c, t):
    fa.layout(c, 1, "Tak NIE wchodzisz", "Bez kasku, kamizelki i butów ochronnych nie wolno wejść na teren pracy.", RED)
    ground = 545
    points = person(c, 470, ground)
    for i, (key, _, missing) in enumerate(ITEMS):
        callout(c, points[key], missing, False, 210 + i * 120, alpha=ramp(t, 0.6 + i * 0.7, 0.3))
    k = ramp(t, 3.2, 0.4)
    if k > 0:
        cross(c, 250, 330, 70 * k)


def dressing(c, t):
    fa.layout(c, 2, "Załóż środki ochrony", "Kask zapięty pod brodą, kamizelka zapięta, buty z podnoskiem i podeszwą antypoślizgową.", GREEN)
    ground = 545
    gear = [ramp(t, 0.5, 0.8), ramp(t, 2.2, 0.8), ramp(t, 3.9, 0.8)]
    points = person(c, 470, ground, *gear)
    for i, (key, label, _) in enumerate(ITEMS):
        callout(c, points[key], label, True, 210 + i * 120, alpha=ramp(t, 1.2 + i * 1.7, 0.3))
    if t > 5.6:
        check(c, 250, 330, 60 * ramp(t, 5.6, 0.4) + 1)


def compare(c, t):
    fa.layout(c, 3, "Sprawdź się przed wejściem", "Brakuje czegoś? Weź z magazynu lub zgłoś przełożonemu, zanim wejdziesz.", ACCENT)
    ground = 545
    person(c, 380, ground)
    person(c, 900, ground, 1, 1, 1, wave=0.5 + 0.5 * math.sin(t * 4))
    cross(c, 380, 380, 110, width=18)
    check(c, 1110, 300, 46)
    c.text(170, 380, "Źle", 38, fill=RED, anchor="mm")
    c.text(1110, 380, "Dobrze", 34, fill=GREEN, anchor="mm")


def finish(c, t):
    c.header = (0, "Kask · Kamizelka · Buty", "Przykładowy film Kiosku: podmień go na własne zasady BHP.", ACCENT)
    c.d.rectangle(c.p(0, 0, W, H), fill=PANEL)
    person(c, 640, 545, 1, 1, 1, wave=0.5 + 0.5 * math.sin(t * 4))
    for i, x in enumerate((360, 920)):
        if t > 0.4 + i * 0.3:
            check(c, x, 330, 50)
    c.text(360, 420, "Bezpiecznie", 28, fill=GREEN, anchor="mm")
    c.text(920, 420, "Widocznie", 28, fill=GREEN, anchor="mm")


SCENES = [(intro, 4.5), (without, 6), (dressing, 8), (compare, 6), (finish, 4.5)]


def main(out):
    total = sum(d for _, d in SCENES)
    ff = subprocess.Popen(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24",
                           "-s", f"{W}x{H}", "-r", str(FPS), "-i", "-", "-c:v", "libx264", "-preset", "slow", "-crf", "24",
                           "-pix_fmt", "yuv420p", "-movflags", "+faststart", out], stdin=subprocess.PIPE)
    start = 0.0
    for scene, length in SCENES:
        for f in range(int(length * FPS)):
            t = f / FPS
            c = fa.compose(scene, t)
            c.rrect(0, H - 8, W * (start + t) / total, H, 0, ACCENT)
            img = c.frame()
            fade = min(1, t / 0.35, (length - t) / 0.25)
            if fade < 1:
                img = Image.blend(Image.new("RGB", (W, H), fa.BACK), img, max(0, fade))
            ff.stdin.write(img.tobytes())
        start += length
    ff.stdin.close()
    ff.wait()


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "bhp-ochrona.mp4")

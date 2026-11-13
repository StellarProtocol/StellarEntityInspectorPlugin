"""Generate the Entity Inspector window's glyph icons (Icons/<name>-<N>.png).

WHITE on transparent: the plugin tints each one at draw time (ImageElement.Tint, fw 2.30.0), so the glyphs follow the
theme (accent / success / muted). House icon rule (Stellar-Design-Language §3): ONE pre-drawn, pixel-hinted picture per
on-screen size, never one master scaled down. Each glyph is drawn in a 24-unit design grid, mapped onto N pixels with
every point snapped so a stroke's edges sit on whole pixels (odd strokes centre on .5, even strokes on whole pixels);
only diagonals and arcs get anti-aliased (8x supersample, box-filtered down). Same pipeline as
StellarWardrobeLoadout/tools/gen_icons.py.

Glyphs:
  radar  - the "Nearby" data tag: a centre dot + two arcs (data that needs the player in range)
  card   - the "Profile" data tag: an ID card (from the player's profile reply)
  hatch  - the undyed "Default" swatch: a rounded square with diagonal hatching
  close  - the round x button of the popovers
  pin    - the Location empty-state tile (a map pin)
  person - the header tile when the class is unknown (a generic person)

Sizes: the glyph's size in canvas units x the UI scales the game runs at (canvas -> screen 0.75 at 1080p, 1.0 at
1440p, 1.25 / 1.5 / 2.0 above), plus 16 and 32. The plugin picks the bitmap nearest to units x live scale.

Run (conda python, from the plugin root):  python tools/gen_icons.py
"""
import os
from PIL import Image, ImageDraw

SS = 8
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Icons")
SCALES = [0.75, 1.0, 1.25, 1.5, 2.0]

# name -> glyph size in canvas units (what the window draws). Keep in sync with Plugin.Style.Blocks.cs.
UNITS = {"radar": 12, "card": 12, "hatch": 12, "close": 14, "pin": 16, "person": 24}


def sizes_for(units):
    return sorted({int(round(units * s)) for s in SCALES} | {16, 32})


def stroke_px(n):
    """Whole-pixel stroke for an N px glyph (~2/24 of the box, never below 1)."""
    return 1 if n < 15 else 2 if n < 27 else 3 if n < 40 else 4


def snap(v, n, w):
    """24-unit design coordinate -> pixel coordinate on the N px grid, snapped so a w px stroke's edges are whole."""
    p = v * n / 24.0
    return (int(p) + 0.5) if w % 2 else float(round(p))


class Pen:
    def __init__(self, n):
        self.n = n
        self.w = stroke_px(n)
        self.img = Image.new("L", (n * SS, n * SS), 0)
        self.d = ImageDraw.Draw(self.img)

    def pt(self, x, y):
        return (snap(x, self.n, self.w) * SS, snap(y, self.n, self.w) * SS)

    def raw(self, v):
        """Unsnapped design length -> supersampled pixels."""
        return v * self.n / 24.0 * SS

    def line(self, *pts, closed=False):
        p = [self.pt(x, y) for x, y in pts]
        if closed:
            p.append(p[0])
        self.d.line(p, fill=255, width=self.w * SS, joint="curve")
        r = self.w * SS / 2.0   # round caps
        for (x, y) in (p[0], p[-1]):
            self.d.ellipse([x - r, y - r, x + r, y + r], fill=255)

    def arc(self, cx, cy, r, start, end):
        x, y = self.pt(cx, cy)
        rr = self.raw(r)
        self.d.arc([x - rr, y - rr, x + rr, y + rr], start, end, fill=255, width=self.w * SS)

    def dot(self, cx, cy, r):
        x, y = self.pt(cx, cy)
        rr = max(self.raw(r), self.w * SS * 0.75)
        self.d.ellipse([x - rr, y - rr, x + rr, y + rr], fill=255)

    def rect(self, x0, y0, x1, y1, radius, fill=False):
        a, b = self.pt(x0, y0), self.pt(x1, y1)
        rr = self.raw(radius)
        if fill:
            self.d.rounded_rectangle([a, b], rr, fill=255)
        else:
            self.d.rounded_rectangle([a, b], rr, outline=255, width=self.w * SS)

    def done(self):
        alpha = self.img.resize((self.n, self.n), Image.BOX)
        out = Image.new("RGBA", (self.n, self.n), (255, 255, 255, 0))
        out.putalpha(alpha)
        return out


def radar(n):
    p = Pen(n)
    p.dot(12, 15, 2.2)                  # the player
    p.arc(12, 15, 6.5, 225, 315)        # inner signal arc
    p.arc(12, 15, 11.5, 230, 310)       # outer signal arc
    return p.done()


def card(n):
    p = Pen(n)
    p.rect(3, 5, 21, 19, 2)                      # card outline
    p.dot(8.5, 10.5, 1.8)                        # portrait head
    p.line((6, 15.5), (11, 15.5))                # portrait shoulders
    p.line((14, 10), (18, 10))                   # text lines (one line below 16 px: two merge)
    if n >= 16:
        p.line((14, 14), (18, 14))
    return p.done()


def hatch(n):
    """Undyed swatch: a rounded outline with 45-degree hatching inside (drawn, then clipped to the box)."""
    p = Pen(n)
    p.rect(2, 2, 22, 22, 3)
    mask = Image.new("L", p.img.size, 0)
    md = ImageDraw.Draw(mask)
    a, b = p.pt(2, 2), p.pt(22, 22)
    md.rounded_rectangle([a, b], p.raw(3), fill=255)
    lines = Image.new("L", p.img.size, 0)
    ld = ImageDraw.Draw(lines)
    step = 6 if n >= 16 else 8   # fewer, clearer stripes on the small bitmaps
    for k in range(-24, 25, step):
        ld.line([p.pt(k, 24), p.pt(k + 24, 0)], fill=255, width=max(1, p.w) * SS)
    p.img.paste(255, (0, 0), Image.composite(lines, Image.new("L", p.img.size, 0), mask))
    return p.done()


def pin(n):
    """Map pin: a ring head with a point below it, plus a centre dot."""
    p = Pen(n)
    p.arc(12, 9.5, 6, 150, 390)                  # head (open at the bottom where the point starts)
    p.line((6.8, 12.5), (12, 21), (17.2, 12.5))  # point
    p.dot(12, 9.5, 1.8)
    return p.done()


def person(n):
    """Generic person: a filled head over a filled shoulder dome."""
    p = Pen(n)
    p.dot(12, 8.5, 4.2)
    a, b = p.pt(4.5, 14.5), p.pt(19.5, 30)
    p.d.ellipse([a, b], fill=255)
    p.d.rectangle([(0, p.pt(0, 21)[1]), (p.img.size[0], p.img.size[1])], fill=0)   # cut the dome flat at the bottom
    return p.done()


def close(n):
    p = Pen(n)
    p.line((6, 6), (18, 18))
    p.line((18, 6), (6, 18))
    return p.done()


DRAW = {"radar": radar, "card": card, "hatch": hatch, "close": close, "pin": pin, "person": person}

if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    for name, units in UNITS.items():
        for n in sizes_for(units):
            DRAW[name](n).save(os.path.join(OUT, f"{name}-{n}.png"), optimize=True)
        print(name, sizes_for(units))

# Builds every Tessera logo file from one geometry: SVG masters, lockups, pixel-snapped small cuts and the
# app/tray .ico files used by the build. Needs Python 3 with Pillow and fontTools; the wordmark is Lato Bold
# (SIL Open Font License, ships with Windows here) turned into outlines, so no font is needed to show it.
#   python brand\build_logo.py
import io, math, os, re, struct

from PIL import Image, ImageDraw
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont

HERE = os.path.dirname(os.path.abspath(__file__))
LOGO = os.path.join(HERE, 'logo')
ICONS = os.path.join(HERE, 'icons')
APP_ICONS = os.path.join(HERE, '..', 'src', 'icons')
FONT = r'C:\Windows\Fonts\Lato-Bold.ttf'

INK, BLUE = '#1B1F24', '#0078D4'           # on light backgrounds
PAPER, SKY = '#F3F4F6', '#4CC2FF'          # on dark backgrounds
LIGHT = {'ink': INK, 'accent': BLUE}
DARK = {'ink': PAPER, 'accent': SKY}


def panes(n, box=None):
    """The three panes for an n-px square, snapped to whole pixels. The gutters between them form the T.
    Returns (role, x0, y0, x1, y1, radii TL/TR/BR/BL). At n=256 this is the master drawing."""
    m = max(1, int(n * 24 / 256))                   # margin
    g = max(2, round(n * 24 / 256))                 # gutter; even so the T's stem sits exactly in the middle
    if g % 2:
        g += 1
    if box:                                         # app icon: draw inside a tile, smaller
        m = box
    inner = n - 2 * m
    top = round((inner - g) * 72 / 184)
    R, r = n * (40 if n >= 48 else 28) / 256, n * 10 / 256  # tighter outer corners keep small panes square
    if box:
        R = r = n * 14 / 256
    y = m + top + g
    half = (inner - g) // 2
    return [('ink', m, m, n - m, m + top, (R, R, r, r)),
            ('ink', m, y, m + half, n - m, (r, r, r, R)),
            ('accent', n - m - half, y, n - m, n - m, (r, r, R, r))]


def num(v):
    return ('%.2f' % v).rstrip('0').rstrip('.')


def rect_path(x0, y0, x1, y1, radii, dx=0.0, dy=0.0, s=1.0):
    tl, tr, br, bl = (q * s for q in radii)
    x0, y0, x1, y1 = x0 * s + dx, y0 * s + dy, x1 * s + dx, y1 * s + dy
    a = lambda q, x, y: 'A%s %s 0 0 1 %s %s' % (num(q), num(q), num(x), num(y))
    return ('M%s %s' % (num(x0 + tl), num(y0)) + 'H' + num(x1 - tr) + a(tr, x1, y0 + tr) +
            'V' + num(y1 - br) + a(br, x1 - br, y1) + 'H' + num(x0 + bl) + a(bl, x0, y1 - bl) +
            'V' + num(y0 + tl) + a(tl, x0 + tl, y0) + 'Z')


def symbol_paths(colors, n=256, dx=0.0, dy=0.0, s=1.0, box=None):
    return ['<path fill="%s" d="%s"/>' % (colors[role], rect_path(x0, y0, x1, y1, rad, dx, dy, s))
            for role, x0, y0, x1, y1, rad in panes(n, box)]


def write_svg(path, w, h, body, title):
    with open(path, 'w', encoding='utf-8') as f:
        f.write('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %s %s"><title>%s</title>%s</svg>\n'
                % (num(w), num(h), title, ''.join(body)))


# ---- wordmark ---------------------------------------------------------------------------------------------
_font = TTFont(FONT)
_glyphs, _cmap = _font.getGlyphSet(), _font.getBestCmap()
XH = _font['OS/2'].sxHeight
TRACK = -10  # font units; Lato's default spacing is a touch loose at display size


def word_layout(text='tessera'):
    x, out = 0, []
    for ch in text:
        g = _glyphs[_cmap[ord(ch)]]
        out.append((g, x))
        x += g.width + TRACK
    bp = BoundsPen(_glyphs)
    for g, gx in out:
        g.draw(TransformPen(bp, (1, 0, 0, 1, gx, 0)))
    return out, bp.bounds  # bounds in font units, y up


def word_path(color, xheight, left, baseline):
    """Wordmark outlines scaled so its x-height is `xheight`, ink starting at x=`left`, sitting on `baseline`."""
    out, (bx0, by0, bx1, by1) = word_layout()
    s = xheight / XH
    pen = SVGPathPen(_glyphs)
    for g, gx in out:
        g.draw(TransformPen(pen, (s, 0, 0, -s, left + (gx - bx0) * s, baseline)))
    d = re.sub(r'-?\d+\.\d+', lambda mo: num(float(mo.group())), pen.getCommands())
    return '<path fill="%s" d="%s"/>' % (color, d), (bx1 - bx0) * s, by1 * s, -by0 * s  # width, ascent, descent


# ---- raster + .ico ----------------------------------------------------------------------------------------
SS = 16  # supersampling: coverage is the exact area average of a 16x16 sub-pixel grid


def rounded_poly(x0, y0, x1, y1, radii, s):
    pts = []
    corners = ((x1, y0, radii[1], -90), (x1, y1, radii[2], 0), (x0, y1, radii[3], 90), (x0, y0, radii[0], 180))
    for cx, cy, q, start in corners:
        q *= s
        ox = cx * s - q if cx == x1 else cx * s + q
        oy = cy * s - q if cy == y1 else cy * s + q
        steps = max(2, int(q))
        for i in range(steps + 1):
            t = math.radians(start + 90 * i / steps)
            pts.append((ox + q * math.cos(t), oy + q * math.sin(t)))
    return pts


def hex_rgb(h):
    return tuple(int(h[i:i + 2], 16) for i in (1, 3, 5))


def render(n, colors, tile=None):
    """RGBA image of the n-px cut. Panes never touch, so each pixel takes one colour with its coverage as alpha."""
    img = Image.new('RGBA', (n, n), (0, 0, 0, 0))
    shapes = []
    if tile:
        shapes.append((tile, (0, 0, n, n, (n * 56 / 256,) * 4)))
    for role, x0, y0, x1, y1, rad in panes(n, box=round(n * 0.2) if tile else None):
        shapes.append((colors[role], (x0, y0, x1, y1, rad)))
    for color, (x0, y0, x1, y1, rad) in shapes:
        mask = Image.new('L', (n * SS, n * SS), 0)
        ImageDraw.Draw(mask).polygon(rounded_poly(x0, y0, x1, y1, rad, SS), fill=255)
        mask = mask.reduce(SS)
        layer = Image.new('RGBA', (n, n), hex_rgb(color) + (255,))
        layer.putalpha(mask)
        img = Image.alpha_composite(img, layer)
    return img


def write_ico(path, images):
    """ICO with 32-bit DIB frames (and PNG for 256), which every Windows API and .NET Framework reads."""
    blobs = []
    for im in images:
        w = im.size[0]
        if w >= 256:
            b = io.BytesIO(); im.save(b, 'PNG'); blobs.append(b.getvalue()); continue
        px = im.transpose(Image.FLIP_TOP_BOTTOM).tobytes('raw', 'BGRA')
        mask_row = ((w + 31) // 32) * 4
        hdr = struct.pack('<IiiHHIIiiII', 40, w, w * 2, 1, 32, 0, len(px) + mask_row * w, 0, 0, 0, 0)
        blobs.append(hdr + px + b'\0' * mask_row * w)
    out = struct.pack('<HHH', 0, 1, len(images))
    off = 6 + 16 * len(images)
    for im, b in zip(images, blobs):
        w = im.size[0]
        out += struct.pack('<BBBBHHII', w % 256, w % 256, 0, 0, 1, 32, len(b), off)
        off += len(b)
    with open(path, 'wb') as f:
        f.write(out + b''.join(blobs))


def main():
    for d in (LOGO, ICONS, APP_ICONS):
        os.makedirs(d, exist_ok=True)
    T = 'Tessera logo'

    # Symbol: master drawing on 256, plus reversed colours for dark backgrounds.
    for suffix, colors in (('', LIGHT), ('-reversed', DARK)):
        write_svg(os.path.join(LOGO, 'tessera-symbol%s.svg' % suffix), 256, 256, symbol_paths(colors), T)

    # Wordmark and lockups. Horizontal: the wordmark's x-height equals the lower panes and sits on their baseline.
    lower = panes(256)[1]
    xh = lower[4] - lower[2]
    _, w, asc, desc = word_path(INK, xh, 0, 0)
    for suffix, colors in (('', LIGHT), ('-reversed', DARK)):
        word, w, asc, desc = word_path(colors['ink'], xh, 0, asc)
        write_svg(os.path.join(LOGO, 'tessera-wordmark%s.svg' % suffix), w, asc + desc, [word], T)

        gap = 56
        word, *_ = word_path(colors['ink'], xh, 232 + gap, lower[4])
        write_svg(os.path.join(LOGO, 'tessera-horizontal%s.svg' % suffix), 232 + gap + w + 24, 256,
                  symbol_paths(colors) + [word], T)

        sxh = xh * 300 / w          # stacked: wordmark a little wider than the symbol
        word, sw, sasc, sdesc = word_path(colors['ink'], sxh, 0, 0)
        width = max(sw, 208) + 48
        top = 256 - 24 + 40 + sasc  # symbol, 40 gap, then the wordmark's ascent
        word, *_ = word_path(colors['ink'], sxh, (width - sw) / 2, top)
        write_svg(os.path.join(LOGO, 'tessera-stacked%s.svg' % suffix), width, top + sdesc + 24,
                  symbol_paths(colors, dx=(width - 256) / 2) + [word], T)

    # App icon: the mark on a dark tile, so it reads on light and dark backgrounds alike.
    write_svg(os.path.join(LOGO, 'tessera-app-icon.svg'), 256, 256,
              ['<rect width="256" height="256" rx="56" fill="%s"/>' % INK] +
              symbol_paths(DARK, box=round(256 * 0.2)), T)

    # Pixel-snapped small cuts (documented as SVG, used as raster in the .ico files).
    for n in (16, 20, 24, 32):
        for suffix, colors in (('', LIGHT), ('-reversed', DARK)):
            write_svg(os.path.join(ICONS, 'tessera-%d%s.svg' % (n, suffix)), n, n, symbol_paths(colors, n), T)

    tray = (16, 20, 24, 32, 40, 48, 64)
    write_ico(os.path.join(APP_ICONS, 'tray-light.ico'), [render(n, LIGHT) for n in tray])
    write_ico(os.path.join(APP_ICONS, 'tray-dark.ico'), [render(n, DARK) for n in tray])
    write_ico(os.path.join(APP_ICONS, 'app.ico'), [render(n, DARK, tile=INK) for n in tray + (256,)])
    for n in (16, 24, 32, 256):
        render(n, LIGHT).save(os.path.join(ICONS, 'tray-light-%d.png' % n))
        render(n, DARK).save(os.path.join(ICONS, 'tray-dark-%d.png' % n))
        render(n, DARK, tile=INK).save(os.path.join(ICONS, 'app-%d.png' % n))
    print('logo files written to', HERE)


if __name__ == '__main__':
    main()

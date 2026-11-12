"""Build Assets/app.ico (the exe / Start menu / installer / window icon) from the tray glyph.

The tray icon is a white monitor glyph for the dark taskbar; on a light Start menu or installer it
would be invisible. The app icon puts the same glyph on a rounded blue tile. Re-run after changing
tray.ico:  python windows/tools/make-app-icon.py
"""
import os
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HERE, '..', 'src', 'AutoSwitchKVM.App', 'Assets')
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
TOP, BOTTOM = (43, 125, 233), (22, 88, 196)   # blue gradient, top -> bottom


def tile(size: int) -> Image.Image:
    scale = 4  # draw large, then downsample for smooth edges
    big = size * scale
    grad = Image.new('RGBA', (big, big))
    draw = ImageDraw.Draw(grad)
    for y in range(big):
        t = y / max(1, big - 1)
        draw.line([(0, y), (big, y)], fill=tuple(int(a + (b - a) * t) for a, b in zip(TOP, BOTTOM)) + (255,))
    mask = Image.new('L', (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, big - 1, big - 1], radius=int(big * 0.22), fill=255)
    grad.putalpha(mask)
    return grad.resize((size, size), Image.LANCZOS)


def glyph(size: int) -> Image.Image:
    src = Image.open(os.path.join(ASSETS, 'tray.ico'))
    src.size = (256, 256)
    src.load()
    return src.convert('RGBA').resize((size, size), Image.LANCZOS)


frames = []
for s in SIZES:
    base = tile(s)
    g = glyph(max(8, round(s * 0.78)))
    base.alpha_composite(g, ((s - g.width) // 2, (s - g.height) // 2))
    frames.append(base)

out = os.path.join(ASSETS, 'app.ico')
frames[-1].save(out, format='ICO', sizes=[(s, s) for s in SIZES], append_images=frames[:-1])
print('wrote', os.path.normpath(out))
# 256 px PNG for notifications (AppNotificationManager display icon).
png = os.path.join(ASSETS, 'app.png')
frames[-1].save(png, format='PNG')
print('wrote', os.path.normpath(png))

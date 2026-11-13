"""Build src/assets/og.png (1200x630 social preview). Re-run after changing the copy:
    python website/tools/make-og.py
Needs Pillow and a bold system font (Segoe UI on Windows; pass a .ttf path as argv[1] elsewhere).
"""
import os, sys
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HERE, '..', 'src', 'assets')
BOLD = sys.argv[1] if len(sys.argv) > 1 else r'C:\Windows\Fonts\segoeuib.ttf'
SEMI = sys.argv[2] if len(sys.argv) > 2 else r'C:\Windows\Fonts\seguisb.ttf'

W, H = 1200, 630
img = Image.new('RGB', (W, H), (255, 247, 236))
d = ImageDraw.Draw(img)
# Playful blobs.
d.ellipse([860, -160, 1360, 340], fill=(219, 232, 255))
d.ellipse([-140, 440, 260, 840], fill=(255, 225, 219))
d.ellipse([980, 470, 1180, 670], fill=(212, 245, 231))

icon = Image.open(os.path.join(ASSETS, 'icon.png')).convert('RGBA').resize((120, 120), Image.LANCZOS)
img.paste(icon, (80, 90), icon)
d.text((222, 112), 'AutoSwitch KVM', font=ImageFont.truetype(BOLD, 52), fill=(43, 37, 64))

title = ImageFont.truetype(BOLD, 84)
d.text((80, 262), 'Press the KVM button.', font=title, fill=(43, 37, 64))
y2 = 362
line2 = 'Your devices follow.'
w2 = d.textlength(line2, font=title)
d.rectangle([76, y2 + 58, 84 + w2, y2 + 92], fill=(255, 207, 74))
d.text((80, y2), line2, font=title, fill=(43, 37, 64))

d.text((80, 510), 'Free for macOS and Windows  ·  autoswitchkvm.com', font=ImageFont.truetype(SEMI, 34), fill=(95, 88, 115))
out = os.path.join(ASSETS, 'og.png')
img.save(out, optimize=True)
print('wrote', os.path.normpath(out))

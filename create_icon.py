"""Generate DartsMob.ico icon file"""
from PIL import Image, ImageDraw

size = 256
img = Image.new('RGBA', (size, size), (0, 0, 0, 0))
draw = ImageDraw.Draw(img)

cx, cy = size // 2, size // 2

# Background circle (dark blue)
draw.ellipse([10, 10, size-10, size-10], fill='#1a1a2e', outline='#d4a84b', width=4)

# Dartboard rings
for r in [100, 80, 60, 40]:
    draw.ellipse([cx-r, cy-r, cx+r, cy+r], outline='#3a3a5a', width=2)

# Bullseye
draw.ellipse([cx-25, cy-25, cx+25, cy+25], fill='#00d4ff')
draw.ellipse([cx-10, cy-10, cx+10, cy+10], fill='#ff4444')

# Dart (simple triangle pointing at bullseye)
draw.polygon([(cx+50, cy-50), (cx+15, cy-15), (cx+45, cy-25)], fill='#d4a84b')
draw.polygon([(cx+50, cy-50), (cx+25, cy-45), (cx+15, cy-15)], fill='#b38b3a')
draw.line([(cx+50, cy-50), (cx+90, cy-90)], fill='#888888', width=5)

# Save as ICO with multiple sizes
img.save('DartsMob.ico', format='ICO', sizes=[(256, 256), (48, 48), (32, 32), (16, 16)])
print('DartsMob.ico created successfully!')

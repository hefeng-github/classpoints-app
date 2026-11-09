import struct, os

SIZES = [16, 24, 32, 48, 64, 128, 256]
BG = (0x1C, 0x22, 0x2B, 255)      # 卡片底
FG = (0x4C, 0xC2, 0xFF, 255)      # 强调青
ACC = (0x34, 0xD3, 0x99, 255)     # 加分绿

def rounded(x, y, w, h, r):
    # 圆角矩形内部判定
    cx = min(max(x, r), w - 1 - r)
    cy = min(max(y, r), h - 1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r

def make(size):
    px = [[(0, 0, 0, 0)] * size for _ in range(size)]
    r = max(2, size // 6)
    # 圆角底板
    for y in range(size):
        for x in range(size):
            if rounded(x, y, size, size, r):
                px[y][x] = BG
    # 白色加号
    arm = max(2, size // 9)
    cx, cy = size // 2, size // 2
    half = max(3, size // 3)
    for y in range(cy - half, cy + half + 1):
        for x in range(cx - arm, cx + arm):
            if 0 <= x < size and 0 <= y < size:
                px[y][x] = FG
    for y in range(cy - arm, cy + arm):
        for x in range(cx - half, cx + half + 1):
            if 0 <= x < size and 0 <= y < size:
                px[y][x] = FG
    # 右下角小圆点（加分绿）
    dot = max(3, size // 5)
    ox, oy = size - dot - max(1, size // 12), size - dot - max(1, size // 12)
    for y in range(oy, oy + dot):
        for x in range(ox, ox + dot):
            if 0 <= x < size and 0 <= y < size:
                px[y][x] = ACC
    return px

def bmp_bytes(px, size):
    # BITMAPINFOHEADER，高度写两倍（XOR + AND 掩码）
    header = struct.pack('<IiiHHIIiiII', 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    body = bytearray()
    for y in range(size - 1, -1, -1):
        for x in range(size):
            b, g, r_, a = px[y][x]
            body += bytes((b, g, r_, a))
    mask_row = ((size + 31) // 32) * 4
    body += bytes(mask_row * size)
    return header + bytes(body)

entries = []
for s in SIZES:
    data = bmp_bytes(make(s), s)
    entries.append((s, data))

out = bytearray(struct.pack('<HHH', 0, 1, len(entries)))
offset = 6 + 16 * len(entries)
blobs = []
for s, data in entries:
    dim = 0 if s >= 256 else s
    out += struct.pack('<BBBBHHII', dim, dim, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
    blobs.append(data)
for b in blobs:
    out += b

os.makedirs('src/ClassPoints.App/Assets', exist_ok=True)
open('src/ClassPoints.App/Assets/app.ico', 'wb').write(bytes(out))
print('ico bytes', len(out))

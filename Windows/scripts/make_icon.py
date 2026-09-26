#!/usr/bin/env python3
"""生成 Windows 程序图标 app.ico（蓝色圆角底 + 白色盾牌 + 锁），纯 Python，无第三方依赖。

    python3 scripts/make_icon.py src/VPNAutoConnect/Resources/app.ico
"""
import math
import struct
import sys
import zlib

TOP = (0.10, 0.55, 0.95)
BOTTOM = (0.05, 0.30, 0.70)


def in_round_rect(x, y, x0, y0, x1, y1, r):
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r and x0 <= x <= x1 and y0 <= y <= y1


def in_shield(x, y):
    top, mid, bottom, half = 0.20, 0.50, 0.85, 0.24
    if y < top or y > bottom:
        return False
    if y <= mid:
        # 顶边中间略微凸起
        w = half
        return abs(x - 0.5) <= w and y >= top + 0.04 * (abs(x - 0.5) / half) ** 2 - 0.02
    t = (y - mid) / (bottom - mid)
    return abs(x - 0.5) <= half * math.cos(t * math.pi / 2)


def in_lock(x, y):
    # 锁体
    if in_round_rect(x, y, 0.395, 0.475, 0.605, 0.665, 0.025):
        return True
    # 锁梁（半圆环 + 两条竖边）
    cx, cy, ro, ri = 0.5, 0.435, 0.078, 0.045
    d = math.hypot(x - cx, y - cy)
    if y <= cy and ri <= d <= ro:
        return True
    if cy <= y <= 0.48 and (ri <= abs(x - cx) <= ro):
        return True
    return False


def color_at(x, y):
    """返回 (r, g, b, a)，取值 0~1"""
    if not in_round_rect(x, y, 0.06, 0.06, 0.94, 0.94, 0.19):
        return None
    t = (y - 0.06) / 0.88
    bg = tuple(TOP[i] + (BOTTOM[i] - TOP[i]) * t for i in range(3))
    if in_shield(x, y) and not in_lock(x, y):
        return (1.0, 1.0, 1.0)
    return bg


def render(size, ss=4):
    rows = []
    for py in range(size):
        row = bytearray([0])  # PNG filter: none
        for px in range(size):
            r = g = b = a = 0.0
            for sy in range(ss):
                for sx in range(ss):
                    c = color_at((px + (sx + 0.5) / ss) / size, (py + (sy + 0.5) / ss) / size)
                    if c:
                        r += c[0]; g += c[1]; b += c[2]; a += 1
            n = ss * ss
            if a:
                row += bytes([round(r / a * 255), round(g / a * 255), round(b / a * 255), round(a / n * 255)])
            else:
                row += bytes(4)
        rows.append(bytes(row))
    return png(size, size, b"".join(rows))


def png(w, h, raw):
    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def ico(images):
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, blobs = b"", b""
    for size, data in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    return header + entries + blobs


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else "app.ico"
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    with open(out, "wb") as f:
        f.write(ico([(s, render(s, 4 if s >= 64 else 8)) for s in sizes]))
    print("完成：" + out)

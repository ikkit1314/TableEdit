"""Draws the TableEdit app icon procedurally and writes TableEdit.icns (no third-party deps)."""
import struct
import sys
import zlib
from pathlib import Path


def png(size: int) -> bytes:
    def px(x: float, y: float):
        s = size
        m = s * 0.09                      # outer margin (macOS icon grid)
        r = s * 0.18                      # corner radius
        x0, y0, x1, y1 = m, m, s - m, s - m
        # rounded-rect coverage
        cx = min(max(x, x0 + r), x1 - r)
        cy = min(max(y, y0 + r), y1 - r)
        d = ((x - cx) ** 2 + (y - cy) ** 2) ** 0.5
        if d > r + 0.5:
            return (0, 0, 0, 0)
        alpha = 255 if d < r - 0.5 else int(255 * (r + 0.5 - d))
        t = (y - y0) / (y1 - y0)
        bg = (int(36 + 20 * t), int(160 - 40 * t), int(110 - 20 * t))
        # white sheet with grid
        sx0, sy0, sx1, sy1 = s * 0.24, s * 0.25, s * 0.76, s * 0.75
        if sx0 <= x <= sx1 and sy0 <= y <= sy1:
            lw = max(1.0, s * 0.012)
            fx, fy = (x - sx0) / (sx1 - sx0), (y - sy0) / (sy1 - sy0)
            if fy < 0.2:
                return (210, 240, 225, alpha)
            on_line = any(abs(fx - v) * (sx1 - sx0) < lw for v in (1 / 3, 2 / 3)) or \
                any(abs(fy - v) * (sy1 - sy0) < lw for v in (0.2, 0.4, 0.6, 0.8))
            return (*((120, 175, 150) if on_line else (255, 255, 255)), alpha)
        return (*bg, alpha)

    raw = bytearray()
    for y in range(size):
        raw.append(0)
        for x in range(size):
            raw.extend(px(x + 0.5, y + 0.5))

    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + chunk(b"IEND", b""))


def main(out: Path) -> None:
    entries = [(b"ic07", 128), (b"ic08", 256), (b"ic09", 512), (b"ic10", 1024)]
    body = b""
    for tag, size in entries:
        data = png(size)
        body += tag + struct.pack(">I", len(data) + 8) + data
    out.write_bytes(b"icns" + struct.pack(">I", len(body) + 8) + body)
    print(f"wrote {out}")


if __name__ == "__main__":
    main(Path(sys.argv[1]))

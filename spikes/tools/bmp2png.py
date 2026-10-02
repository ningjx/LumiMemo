# Convert the 24-bit BMPs produced by screenshot.py to PNG (stdlib only).
# Usage: python bmp2png.py <in.bmp> <out.png> [max_width]
import struct
import sys
import zlib


def read_bmp(path):
    with open(path, "rb") as f:
        data = f.read()
    assert data[:2] == b"BM"
    offset = struct.unpack_from("<I", data, 10)[0]
    header_size, w, h, planes, bpp, comp = struct.unpack_from("<IiiHHI", data, 14)
    assert bpp == 24 and comp == 0
    top_down = h < 0
    h = abs(h)
    row_size = (w * 3 + 3) & ~3
    rows = []
    for y in range(h):
        src_y = y if top_down else (h - 1 - y)
        start = offset + src_y * row_size
        row = bytearray()
        px = data[start:start + w * 3]
        for i in range(0, w * 3, 3):
            row += bytes((px[i + 2], px[i + 1], px[i]))  # BGR -> RGB
        rows.append(bytes(row))
    return w, h, rows


def write_png(path, w, h, rows):
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(tag, payload):
        c = struct.pack(">I", len(payload)) + tag + payload
        return c + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 6))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


def downscale(w, h, rows, max_width):
    if w <= max_width:
        return w, h, rows
    factor = max_width / w
    nw, nh = max_width, int(h * factor)
    out = []
    for y in range(nh):
        src = rows[int(y / factor)]
        row = bytearray()
        for x in range(nw):
            i = int(x / factor) * 3
            row += src[i:i + 3]
        out.append(bytes(row))
    return nw, nh, out


def main():
    src, dst = sys.argv[1], sys.argv[2]
    max_width = int(sys.argv[3]) if len(sys.argv) > 3 else 1440
    w, h, rows = read_bmp(src)
    w, h, rows = downscale(w, h, rows, max_width)
    write_png(dst, w, h, rows)
    print(f"png={dst} {w}x{h}")


if __name__ == "__main__":
    main()

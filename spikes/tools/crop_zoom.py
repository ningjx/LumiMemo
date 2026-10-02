# Crop a region from a BMP and save as PNG, optionally scaled up (nearest neighbor).
# Usage: python crop_zoom.py <in.bmp> <out.png> <x> <y> <w> <h> [zoom]
import sys

sys.path.insert(0, __file__.rsplit("\\", 1)[0])
from bmp2png import read_bmp, write_png


def main():
    src, dst = sys.argv[1], sys.argv[2]
    x, y, cw, ch = (int(v) for v in sys.argv[3:7])
    zoom = int(sys.argv[6 + 1]) if len(sys.argv) > 7 else 1
    w, h, rows = read_bmp(src)
    crop = [r[x * 3:(x + cw) * 3] for r in rows[y:y + ch]]
    if zoom > 1:
        zoomed = []
        for r in crop:
            zr = bytearray()
            for i in range(0, len(r), 3):
                zr += r[i:i + 3] * zoom
            for _ in range(zoom):
                zoomed.append(bytes(zr))
        crop = zoomed
        cw *= zoom
        ch *= zoom
    write_png(dst, cw, ch, crop)
    print(f"png={dst} {cw}x{ch}")


if __name__ == "__main__":
    main()
